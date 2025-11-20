using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
namespace EventMailService.Services;
public sealed class SqlDbFactory(IConfiguration cfg) : IDbFactory
{
    public SqlConnection Create() => new(cfg.GetConnectionString("MainDb"));
}