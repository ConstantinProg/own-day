using Microsoft.EntityFrameworkCore;

namespace OwnDay.Infrastructure;

public sealed class OwnDayDbContext(DbContextOptions<OwnDayDbContext> options) : DbContext(options)
{
}
