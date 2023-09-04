using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using System.Text;

namespace Client;

internal class Program
{

    static readonly string postTemplate = """
        {{
            "ClaimID": {0},
            "ClaimNumber": {0},
            "Amount": {1},
            "Command": "P",
            "IsolationLevel": "Snapshot",
            "WorkerId": {2}
        }}
        """;
    static async Task Main(string[] args)
    {

        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddHttpClient();
        var host = builder.Build();

        var numclaims = 10;
        var scopeCount = 50;

        var scopes = Enumerable.Range(0, scopeCount)
            .Select(i => host.Services.CreateScope())
            .ToArray();

        for (int i = 0; i < 1; i++)
        {
            var responseTasks = new List<Task<HttpResponseMessage>>();

            for (int j = 0; j < scopeCount; j++)
            {
                
                var client = scopes[j].ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
                //var stringContent = new StringContent(postTemplate.Replace("%1", (i * 100).ToString()));
                var stringContent = string.Format(postTemplate, (j % numclaims) + 1, (i + 1) * 100, j);
                Console.WriteLine(stringContent);
                var jsonContent = new StringContent(stringContent, Encoding.UTF8, "application/json");
                //responseTasks.Add(client.PostAsync("http://localhost:5000/api/pay", jsonContent));
                responseTasks.Add(client.PostAsync("http://localhost:5288/api/pay", jsonContent));
            }

            await Task.WhenAll(responseTasks);

            int x = 1;
            foreach (var responseTask in responseTasks)
            {
                Console.WriteLine($"Response {x++}");
                //var response = await responseTask;
                var content = await responseTask.Result.Content.ReadAsStringAsync();
                Console.WriteLine(content);
            }
        }

    }
}
