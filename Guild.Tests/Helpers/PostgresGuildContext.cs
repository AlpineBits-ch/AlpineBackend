using Guild.Persistence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Guild.Tests.Helpers;

/// <summary>
/// A <see cref="MicroserviceContext"/> configured with the <b>real Npgsql provider</b>, pointed at a
/// host that is never contacted.
///
/// <para><b>Why this exists.</b> Every other test in this project uses
/// <see cref="TestGuildContext"/>, which is the EF InMemory provider. InMemory does not translate
/// LINQ to SQL - it runs the expression tree in-process with LINQ-to-Objects. That makes it useful
/// for asserting behaviour and structurally incapable of catching an untranslatable query. A query
/// that throws <c>InvalidOperationException: The LINQ expression ... could not be translated</c> in
/// production passes green under InMemory every time. That is exactly how the inbox 500 shipped.</para>
///
/// <para><b>Why there is no database.</b> Translation happens when the query is compiled, before a
/// connection is ever opened, so <see cref="EntityFrameworkQueryableExtensions.ToQueryString"/>
/// raises the same exception the server would - with no Docker, no container, and no fixture, in
/// about a millisecond. Anything that needs rows back needs a real Postgres; anything that only
/// needs to know "does this compile to SQL" belongs here.</para>
///
/// <para>The base <c>OnConfiguring</c> is deliberately <em>not</em> overridden: it carries the enum
/// mappings and the snake-case convention, and a translation test run against a differently
/// configured model would be testing a model that is not the one deployed.</para>
/// </summary>
internal sealed class PostgresGuildContext() : MicroserviceContext(
    new DbContextOptionsBuilder<MicroserviceContext>().Options);
