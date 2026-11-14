using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ANalyze.Infrastructure.Data;

/// <summary>Used by `dotnet ef` only (SQLite migrations in Data/Migrations).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AnalyzeDbContext>
{
    public AnalyzeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AnalyzeDbContext>().UseSqlite("Data Source=design.db").Options);
}

/// <summary>Used by `dotnet ef --context PostgresDbContext` only (PostgreSQL migrations in Data/PostgresMigrations); no server needed.</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresDbContext>
{
    public PostgresDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresDbContext>().UseNpgsql("Host=localhost;Database=analyze_design").Options);
}
