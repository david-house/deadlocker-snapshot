using ClaimTransWorker.Models;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace ClaimTransWorker;

internal class Program
{
    static void Main(string[] args)
    {
        var preDelayMs = int.Parse(args[0]);
        var postDelayMs = int.Parse(args[1]);
        var midDelayMs = int.Parse(args[2]);
        var command = args[3];
        var isolationLevel = (IsolationLevel)Enum.Parse(typeof(IsolationLevel), args[3]);
        
        Console.WriteLine("Hello, World!");
        var context = new sutContext();
        var trx = context.Database.BeginTransaction(isolationLevel);
        var claims = context.Claims
            .Include(c => c.ClaimTransactions)
            .ToList();
        trx.Commit();

    }
}
