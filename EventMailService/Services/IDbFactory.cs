using Microsoft.Data.SqlClient;
namespace EventMailService.Services;
public interface IDbFactory { SqlConnection Create(); }