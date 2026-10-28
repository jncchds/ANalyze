using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ANalyze.Infrastructure.Data;

/// <summary>Used by `dotnet ef` only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AnalyzeDbContext>
{
    public AnalyzeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AnalyzeDbContext>().UseSqlite("Data Source=design.db").Options);
}
