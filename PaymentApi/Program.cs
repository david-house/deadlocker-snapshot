using PaymentApi.Models;
using System.ComponentModel.DataAnnotations;
//using Microsoft.Extensions.Logging;
using NLog.Extensions.Logging;
namespace PaymentApi;

public class Program
{
    
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        
        //File.WriteAllText("before.txt", builder.Configuration.GetDebugView());
        
        builder.Configuration.AddEnvironmentVariables(prefix: "SUT_");
        builder.Services.AddControllers();

        builder.Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.SetMinimumLevel(LogLevel.Information);
            loggingBuilder.AddFilter("Microsoft", LogLevel.Warning);
            loggingBuilder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
            loggingBuilder.AddNLog();
        });
        var cfg = builder.Configuration;
        var cs = cfg.GetConnectionString("sqlhost0");
        //var section = cfg.GetSection("ConnectionStrings");
        //var sections = cfg.GetDebugView();
        //var fi = new FileInfo("after.txt");
        //File.WriteAllText(fi.FullName, sections);
        Console.WriteLine($"Connection string: {cs}");
        Console.WriteLine("Hello World!");
        builder.Services.AddSqlServer<SutContext>(cs);

        var app = builder.Build();



        app.MapControllers();

        app.Run();
    }
}
