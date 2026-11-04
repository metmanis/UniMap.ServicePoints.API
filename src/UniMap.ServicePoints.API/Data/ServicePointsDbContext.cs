using Microsoft.EntityFrameworkCore;

namespace UniMap.ServicePoints.API.Data;

// Minimal context: it only owns the migrations. Entities come in later tasks.
public class ServicePointsDbContext(DbContextOptions<ServicePointsDbContext> options) : DbContext(options)
{
}
