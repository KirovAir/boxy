using Microsoft.EntityFrameworkCore.Design;

namespace Boxy.Data;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=boxy.db")
            .Options;
        return new AppDbContext(options);
    }
}
