using System.Net;
using StripeSdk = Stripe;

namespace Billing.Application.Stripe;

/// <summary><see cref="IStripeGateway"/> over Stripe.net.</summary>
public sealed class StripeGateway(StripeOptions options, ILogger<StripeGateway>? logger = null)
    : IStripeGateway
{
    private StripeSdk.StripeClient? _client;

    private StripeSdk.StripeClient Client
    {
        get
        {
            if (_client is not null) return _client;

            if (!options.IsConfigured)
            {
                throw new InvalidOperationException(
                    "A Stripe call was attempted with no STRIPE_SECRET_KEY set. Every caller is "
                    + "supposed to check Env.License.IsStripeConfigured first and do nothing - a "
                    + "hosted instance that has not finished setting Stripe up is a normal state, "
                    + "not an error - so reaching this means a new path skipped that check.");
            }

            return _client = new StripeSdk.StripeClient(options.SecretKey);
        }
    }

    public async Task<StripeObjectRef> CreateProductAsync(
        StripeProductRequest request,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var create = new StripeSdk.ProductCreateOptions
        {
            Name = request.Name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description,
            Metadata = new Dictionary<string, string>(request.Metadata),
        };

        var product = await CallAsync(
            "products.create",
            idempotencyKey,
            requestOptions => Client.V1.Products.CreateAsync(create, requestOptions, cancellationToken));

        logger?.LogInformation(
            "Stripe product {Product} in place for '{Name}' (idempotency key {Key}).",
            product.Id, request.Name, idempotencyKey.Value);

        return new StripeObjectRef(product.Id);
    }

    public async Task<StripeObjectRef> CreatePriceAsync(
        StripePriceRequest request,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var create = new StripeSdk.PriceCreateOptions
        {
            Product = request.ProductId,
            UnitAmount = request.UnitAmountMinorUnits,
            Currency = request.Currency,
            Recurring = new StripeSdk.PriceRecurringOptions { Interval = request.Interval },
            Metadata = new Dictionary<string, string>(request.Metadata),
        };

        var price = await CallAsync(
            "prices.create",
            idempotencyKey,
            requestOptions => Client.V1.Prices.CreateAsync(create, requestOptions, cancellationToken));

        logger?.LogInformation(
            "Stripe price {Price} in place on product {Product} for {Amount} {Currency}/{Interval} "
            + "(idempotency key {Key}).",
            price.Id, request.ProductId, request.UnitAmountMinorUnits, request.Currency,
            request.Interval, idempotencyKey.Value);

        return new StripeObjectRef(price.Id);
    }

    public async Task<StripeSubscriptionSnapshot?> GetSubscriptionAsync(
        string subscriptionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        StripeSdk.Subscription subscription;

        try
        {
            subscription = await Client.V1.Subscriptions.GetAsync(
                subscriptionId, null, null, cancellationToken);
        }
        catch (StripeSdk.StripeException exception)
            when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // A real answer rather than a failure: an event can name a subscription that has since
            // been deleted, and the caller's correct response is to reconcile it away.
            return null;
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("subscriptions.get", exception);
        }

        // The billing period moved onto the subscription item in the 2025-03-31 API version, so it
        // is read from the first item rather than from the subscription.
        var item = subscription.Items?.Data?.FirstOrDefault();

        return new StripeSubscriptionSnapshot(
            subscription.Id,
            subscription.Status,
            subscription.CustomerId,
            item?.Price?.Id,
            item is null ? null : new DateTimeOffset(item.CurrentPeriodEnd, TimeSpan.Zero),
            subscription.CancelAtPeriodEnd,
            subscription.LatestInvoiceId,
            subscription.Metadata ?? new Dictionary<string, string>());
    }

    public async Task<StripeObjectRef> CreateCustomerAsync(
        StripeCustomerRequest request,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var create = new StripeSdk.CustomerCreateOptions
        {
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email,
            Address = ToAddress(request.Address),
            Metadata = new Dictionary<string, string>(request.Metadata),
        };

        var customer = await CallAsync(
            "customers.create",
            idempotencyKey,
            requestOptions => Client.V1.Customers.CreateAsync(create, requestOptions, cancellationToken));

        logger?.LogInformation(
            "Stripe customer {Customer} in place (idempotency key {Key}).",
            customer.Id, idempotencyKey.Value);

        return new StripeObjectRef(customer.Id);
    }

    public async Task UpdateCustomerAddressAsync(
        string customerId,
        StripeAddress address,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentNullException.ThrowIfNull(address);

        var update = new StripeSdk.CustomerUpdateOptions { Address = ToAddress(address) };

        await CallAsync(
            "customers.update",
            idempotencyKey,
            requestOptions => Client.V1.Customers.UpdateAsync(
                customerId, update, requestOptions, cancellationToken));
    }

    public async Task<StripeSubscriptionResult> CreateSubscriptionAsync(
        StripeSubscriptionRequest request,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var create = new StripeSdk.SubscriptionCreateOptions
        {
            Customer = request.CustomerId,
            Items = [new StripeSdk.SubscriptionItemOptions { Price = request.PriceId }],

            // The subscription exists before the card is charged, and stays incomplete until the
            // client confirms and the webhook says otherwise.
            PaymentBehavior = "default_incomplete",
            AutomaticTax = new StripeSdk.SubscriptionAutomaticTaxOptions { Enabled = request.AutomaticTax },
            Metadata = new Dictionary<string, string>(request.Metadata),

            TrialPeriodDays = request.TrialPeriodDays,
            TrialSettings = new StripeSdk.SubscriptionTrialSettingsOptions
            {
                EndBehavior = new StripeSdk.SubscriptionTrialSettingsEndBehaviorOptions
                {
                    MissingPaymentMethod = "cancel",
                },
            },

            DefaultPaymentMethod = string.IsNullOrWhiteSpace(request.DefaultPaymentMethodId)
                ? null
                : request.DefaultPaymentMethodId,

            Expand = ["latest_invoice.confirmation_secret", "pending_setup_intent"],
        };

        var subscription = await CallAsync(
            "subscriptions.create",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.CreateAsync(create, requestOptions, cancellationToken));

        var clientSecret = subscription.LatestInvoice?.ConfirmationSecret?.ClientSecret
                           ?? subscription.PendingSetupIntent?.ClientSecret;

        logger?.LogInformation(
            "Stripe subscription {Subscription} created for customer {Customer} on price {Price} "
            + "(automatic tax {Tax}, trial {Trial}, confirmation secret {Secret}).",
            subscription.Id, request.CustomerId, request.PriceId,
            request.AutomaticTax ? "on" : "off",
            request.TrialPeriodDays is { } days ? $"{days} days" : "none",
            clientSecret is null ? "absent" : "present");

        return new StripeSubscriptionResult(Snapshot(subscription), clientSecret);
    }

    public async Task UpdateSubscriptionMetadataAsync(
        string subscriptionId,
        IReadOnlyDictionary<string, string> metadata,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(metadata);

        var update = new StripeSdk.SubscriptionUpdateOptions
        {
            Metadata = new Dictionary<string, string>(metadata),
        };

        await CallAsync(
            "subscriptions.update.metadata",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.UpdateAsync(
                subscriptionId, update, requestOptions, cancellationToken));
    }

    public async Task<StripeSubscriptionResult?> GetSubscriptionWithSecretAsync(
        string subscriptionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        StripeSdk.Subscription subscription;

        var get = new StripeSdk.SubscriptionGetOptions
        {
            Expand = ["latest_invoice.confirmation_secret"],
        };

        try
        {
            subscription = await Client.V1.Subscriptions.GetAsync(
                subscriptionId, get, null, cancellationToken);
        }
        catch (StripeSdk.StripeException exception)
            when (exception.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("subscriptions.get", exception);
        }

        return new StripeSubscriptionResult(
            Snapshot(subscription), subscription.LatestInvoice?.ConfirmationSecret?.ClientSecret);
    }

    public async Task CancelIncompleteSubscriptionAsync(
        string subscriptionId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        // No invoice and no proration: nothing was ever paid on an incomplete subscription, so
        // there is nothing to settle and asking Stripe to invoice for it would bill for an
        // abandoned attempt.
        var cancel = new StripeSdk.SubscriptionCancelOptions { InvoiceNow = false, Prorate = false };

        await CallAsync(
            "subscriptions.cancel",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.CancelAsync(
                subscriptionId, cancel, requestOptions, cancellationToken));
    }

    public async Task<StripeSubscriptionSnapshot> SetCancelAtPeriodEndAsync(
        string subscriptionId,
        bool cancelAtPeriodEnd,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var update = new StripeSdk.SubscriptionUpdateOptions { CancelAtPeriodEnd = cancelAtPeriodEnd };

        var subscription = await CallAsync(
            "subscriptions.update.cancel_at_period_end",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.UpdateAsync(
                subscriptionId, update, requestOptions, cancellationToken));

        return Snapshot(subscription);
    }

    public async Task<StripeSubscriptionSnapshot> DeferBillingAsync(
        string subscriptionId,
        DateTimeOffset resumeAt,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var update = new StripeSdk.SubscriptionUpdateOptions
        {
            TrialEnd = resumeAt.UtcDateTime,

            // Nothing to prorate: the price is unchanged and the customer is not owed the
            // difference between two of them.
            ProrationBehavior = "none",
        };

        var subscription = await CallAsync(
            "subscriptions.update.defer_billing",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.UpdateAsync(
                subscriptionId, update, requestOptions, cancellationToken));

        return Snapshot(subscription);
    }

    public async Task<StripeSubscriptionSnapshot> ChangeSubscriptionPriceAsync(
        string subscriptionId,
        string priceId,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceId);

        var itemId = await CurrentItemIdAsync(subscriptionId, cancellationToken);

        var update = new StripeSdk.SubscriptionUpdateOptions
        {
            // The existing item is moved rather than a second one added. Adding would bill for both.
            Items = [new StripeSdk.SubscriptionItemOptions { Id = itemId, Price = priceId }],
            ProrationBehavior = "create_prorations",
        };

        var subscription = await CallAsync(
            "subscriptions.update.price",
            idempotencyKey,
            requestOptions => Client.V1.Subscriptions.UpdateAsync(
                subscriptionId, update, requestOptions, cancellationToken));

        return Snapshot(subscription);
    }

    public async Task<StripeProrationPreview> PreviewSubscriptionPriceChangeAsync(
        string subscriptionId, string priceId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(priceId);

        var itemId = await CurrentItemIdAsync(subscriptionId, cancellationToken);

        var preview = new StripeSdk.InvoiceCreatePreviewOptions
        {
            Subscription = subscriptionId,
            SubscriptionDetails = new StripeSdk.InvoiceSubscriptionDetailsOptions
            {
                Items = [new StripeSdk.InvoiceSubscriptionDetailsItemOptions { Id = itemId, Price = priceId }],
                ProrationBehavior = "create_prorations",
            },
        };

        StripeSdk.Invoice invoice;

        try
        {
            invoice = await Client.V1.Invoices.CreatePreviewAsync(preview, null, cancellationToken);
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("invoices.create_preview", exception);
        }

        var lines = new List<StripeProrationLine>();
        var immediate = 0L;

        foreach (var line in invoice.Lines?.Data ?? [])
        {
            lines.Add(new StripeProrationLine(
                string.IsNullOrWhiteSpace(line.Description) ? "Adjustment" : line.Description,
                line.Amount));

            // Only the proration lines are charged now; the rest of the preview is the period that
            // follows and is billed on its own date.
            if (line.Parent?.SubscriptionItemDetails?.Proration == true
                || line.Parent?.InvoiceItemDetails?.Proration == true)
            {
                immediate += line.Amount;
            }
        }

        return new StripeProrationPreview(
            immediate,
            invoice.Currency ?? string.Empty,
            invoice.Total,
            invoice.NextPaymentAttempt is { } attempt
                ? new DateTimeOffset(attempt, TimeSpan.Zero)
                : new DateTimeOffset(invoice.PeriodEnd, TimeSpan.Zero),
            lines);
    }

    public async Task<IReadOnlyList<StripePaymentMethodSummary>> ListPaymentMethodsAsync(
        string customerId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        try
        {
            var customer = await Client.V1.Customers.GetAsync(customerId, null, null, cancellationToken);

            var methods = await Client.V1.PaymentMethods.ListAsync(
                new StripeSdk.PaymentMethodListOptions { Customer = customerId, Type = "card" },
                null,
                cancellationToken);

            var defaultId = customer?.InvoiceSettings?.DefaultPaymentMethodId;

            return methods.Data
                .Select(method => new StripePaymentMethodSummary(
                    method.Id,
                    method.Card?.Brand,
                    method.Card?.Last4,
                    method.Card?.ExpMonth,
                    method.Card?.ExpYear,
                    string.Equals(method.Id, defaultId, StringComparison.Ordinal)))
                .ToList();
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("payment_methods.list", exception);
        }
    }

    public async Task<StripeCardIdentity?> GetCardIdentityAsync(
        string customerId, string? paymentMethodId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        StripeSdk.Customer customer;
        StripeSdk.StripeList<StripeSdk.PaymentMethod> methods;

        try
        {
            customer = await Client.V1.Customers.GetAsync(customerId, null, null, cancellationToken);

            methods = await Client.V1.PaymentMethods.ListAsync(
                new StripeSdk.PaymentMethodListOptions { Customer = customerId, Type = "card" },
                null,
                cancellationToken);
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("payment_methods.list", exception);
        }

        var cards = methods.Data ?? [];

        StripeSdk.PaymentMethod? chosen;

        if (!string.IsNullOrWhiteSpace(paymentMethodId))
        {
            // Matched against this customer's own list rather than fetched by id.
            chosen = cards.FirstOrDefault(
                method => string.Equals(method.Id, paymentMethodId, StringComparison.Ordinal));
        }
        else
        {
            var defaultId = customer?.InvoiceSettings?.DefaultPaymentMethodId;

            chosen = cards.FirstOrDefault(
                         method => string.Equals(method.Id, defaultId, StringComparison.Ordinal))
                     ?? cards.FirstOrDefault();
        }

        if (chosen?.Card?.Fingerprint is not { Length: > 0 } fingerprint) return null;

        logger?.LogDebug(
            "Read the card identity of payment method {PaymentMethod} on Stripe customer {Customer}.",
            chosen.Id, customerId);

        return new StripeCardIdentity(chosen.Id, fingerprint);
    }

    public async Task<string?> CreateSetupIntentAsync(
        string customerId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var create = new StripeSdk.SetupIntentCreateOptions
        {
            Customer = customerId,

            // Off-session, because the card being added is what the next invoice will be charged to
            // while nobody is looking at a browser.
            Usage = "off_session",
        };

        var intent = await CallAsync(
            "setup_intents.create",
            idempotencyKey,
            requestOptions => Client.V1.SetupIntents.CreateAsync(create, requestOptions, cancellationToken));

        return intent.ClientSecret;
    }

    public async Task SetDefaultPaymentMethodAsync(
        string customerId,
        string paymentMethodId,
        StripeIdempotencyKey idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodId);

        var update = new StripeSdk.CustomerUpdateOptions
        {
            InvoiceSettings = new StripeSdk.CustomerInvoiceSettingsOptions
            {
                DefaultPaymentMethod = paymentMethodId,
            },
        };

        await CallAsync(
            "customers.update.default_payment_method",
            idempotencyKey,
            requestOptions => Client.V1.Customers.UpdateAsync(
                customerId, update, requestOptions, cancellationToken));
    }

    public async Task DetachPaymentMethodAsync(
        string paymentMethodId, StripeIdempotencyKey idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodId);

        await CallAsync(
            "payment_methods.detach",
            idempotencyKey,
            requestOptions => Client.V1.PaymentMethods.DetachAsync(
                paymentMethodId, null, requestOptions, cancellationToken));
    }

    public async Task<IReadOnlyList<StripeInvoiceSummary>> ListInvoicesAsync(
        string customerId, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        try
        {
            var invoices = await Client.V1.Invoices.ListAsync(
                new StripeSdk.InvoiceListOptions { Customer = customerId, Limit = limit },
                null,
                cancellationToken);

            return invoices.Data
                .Select(invoice => new StripeInvoiceSummary(
                    invoice.Id,
                    invoice.Number,
                    invoice.Status,
                    invoice.AmountDue,
                    invoice.Currency ?? string.Empty,
                    new DateTimeOffset(invoice.Created, TimeSpan.Zero),
                    invoice.HostedInvoiceUrl,
                    invoice.InvoicePdf))
                .ToList();
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("invoices.list", exception);
        }
    }

    /// <summary>The id of the one item a subscription created here has.</summary>
    private async Task<string> CurrentItemIdAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        StripeSdk.Subscription subscription;

        try
        {
            subscription = await Client.V1.Subscriptions.GetAsync(
                subscriptionId, null, null, cancellationToken);
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed("subscriptions.get", exception);
        }

        var item = subscription.Items?.Data?.FirstOrDefault();

        if (item is null)
        {
            throw new StripeGatewayException(
                "subscriptions.get",
                $"Stripe subscription {subscriptionId} has no items, so there is nothing to reprice. "
                + "This wave creates single-item subscriptions only.");
        }

        return item.Id;
    }

    private static StripeSdk.AddressOptions? ToAddress(StripeAddress? address) =>
        address is null
            ? null
            : new StripeSdk.AddressOptions
            {
                Country = address.Country,
                Line1 = address.Line1,
                Line2 = address.Line2,
                City = address.City,
                State = address.State,
                PostalCode = address.PostalCode,
            };

    /// <summary>The one translation from Stripe's subscription onto ours.</summary>
    private static StripeSubscriptionSnapshot Snapshot(StripeSdk.Subscription subscription)
    {
        var item = subscription.Items?.Data?.FirstOrDefault();

        return new StripeSubscriptionSnapshot(
            subscription.Id,
            subscription.Status,
            subscription.CustomerId,
            item?.Price?.Id,
            item is null ? null : new DateTimeOffset(item.CurrentPeriodEnd, TimeSpan.Zero),
            subscription.CancelAtPeriodEnd,
            subscription.LatestInvoiceId,
            subscription.Metadata ?? new Dictionary<string, string>());
    }

    /// <summary>Runs one Stripe write under its idempotency key and turns a Stripe failure into one
    /// of ours. Every write goes through it, so there is exactly one place where the key is attached
    /// and exactly one place where an exception is translated.</summary>
    private async Task<T> CallAsync<T>(
        string operation,
        StripeIdempotencyKey idempotencyKey,
        Func<StripeSdk.RequestOptions, Task<T>> call)
    {
        var requestOptions = new StripeSdk.RequestOptions { IdempotencyKey = idempotencyKey.Value };

        try
        {
            return await call(requestOptions);
        }
        catch (StripeSdk.StripeException exception)
        {
            throw Failed(operation, exception);
        }
    }

    private static StripeGatewayException Failed(string operation, StripeSdk.StripeException exception) =>
        new(operation,
            $"Stripe refused {operation}: {exception.StripeError?.Message ?? exception.Message} "
            + $"(type {exception.StripeError?.Type ?? "unknown"}, code "
            + $"{exception.StripeError?.Code ?? "none"}).",
            exception,
            exception.StripeError?.Code);
}
