using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HookVault.Infrastructure;

// IDesignTimeDbContextFactory is used by EF Core CLI tools (dotnet ef migrations add) to
// create the DbContext without running the full application. The actual runtime connection
// is configured in Program.cs; this factory is only ever called by design-time tooling.
public sealed class HookVaultDbContextFactory : IDesignTimeDbContextFactory<HookVaultDbContext>
{
    public HookVaultDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<HookVaultDbContext>()
            .UseSqlite("Data Source=hookvault-design-time.db")
            .Options;
        return new HookVaultDbContext(opts);
    }
}
