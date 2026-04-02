using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jellyfin.Plugin.SegmentRecognition.Data;

/// <summary>
/// Design-time factory for <see cref="SegmentDbContext"/>, used by EF Core CLI tools
/// (e.g. <c>dotnet ef migrations add</c>) to create the context without the Jellyfin host.
/// </summary>
public class DesignTimeSegmentDbContextFactory : IDesignTimeDbContextFactory<SegmentDbContext>
{
    /// <inheritdoc />
    public SegmentDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SegmentDbContext>();
        optionsBuilder.UseSqlite("Data Source=design-time.db");
        return new SegmentDbContext(optionsBuilder.Options);
    }
}
