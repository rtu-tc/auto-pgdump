using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;

var builder = Host.CreateDefaultBuilder()
    .ConfigureServices((cxt, services) =>
    {
        services.AddQuartz(q =>
        {
            q.UseMicrosoftDependencyInjectionJobFactory();
        });
        services.AddQuartzHostedService(opt =>
        {
            opt.WaitForJobsToComplete = true;
        });
    }).Build();

var schedulerFactory = builder.Services.GetRequiredService<ISchedulerFactory>();
var scheduler = await schedulerFactory.GetScheduler();

var job = JobBuilder.Create<PgDumpJob>()
    .WithIdentity("pgDumpJob", "default")
    .Build();
var trigger = TriggerBuilder.Create()
    .WithIdentity("cronTrigger", "default")
    .StartNow()
    .WithCronSchedule(GetCronExpression())
    .Build();

await scheduler.ScheduleJob(job, trigger);
await builder.RunAsync();


string GetCronExpression() => Environment.GetEnvironmentVariable("ScheduleCron")!;
class PgDumpJob: IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var (host, port, dbname, user, password) = ParsePostgresConnectionString();
        var response = await Cli.Wrap("pg_dump")
            .WithArguments(["--host", host, "--port", port, "--dbname", dbname, "-U", user, "-f", "dump.sql"])
            .WithEnvironmentVariables(new Dictionary<string, string?>(){{"PGPASSWORD", password}})
            .ExecuteAsync();
        var (url, accessKeyId, secretAccessKey, bucketName) = GetS3Config();
        using var s3Client = new AmazonS3Client(accessKeyId, secretAccessKey, new AmazonS3Config()
        {
            ServiceURL = url,
            ForcePathStyle = true
        });
        await s3Client.PutObjectAsync(new PutObjectRequest()
        {
            InputStream = new FileStream("dump.sql", FileMode.Open),
            BucketName = bucketName,
            Key = $"pg_dump/{dbname}/dump_{TimeProvider.System.GetUtcNow() + TimeSpan.FromHours(3):u}.sql"
        });
        
        await s3Client.PutObjectAsync(new PutObjectRequest()
        {
            InputStream = new FileStream("dump.sql", FileMode.Open),
            BucketName = bucketName,
            Key = $"pg_dump/{dbname}/latest.sql"
        });
    }
    (string host, string port, string dbname, string user, string password) ParsePostgresConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")!;
        var regex = new Regex(@"Host=(?<host>[^;]*);?|Port=(?<port>[^;]*);?|Database=(?<dbname>[^;]*);?|Username=(?<username>[^;]*);?|Password=(?<password>[^;]*);?");

        var matches = regex.Matches(connectionString);
        var parsedConnectionString = new Dictionary<string, string>();
        foreach (var groupMatch in matches)
        {
            var match = regex.Match(groupMatch!.ToString()!);
            var (group, value) = match.Groups.Keys
                .Select(g => (g, match.Groups[g].Value))
                .First(g => g.g != "0" && !string.IsNullOrEmpty(g.Item2));
            parsedConnectionString.Add(group, value);
        }

        return (
            parsedConnectionString["host"],
            parsedConnectionString["port"],
            parsedConnectionString["dbname"],
            parsedConnectionString["username"], 
            parsedConnectionString["password"]
            );
    }
    (string url, string accessKeyId, string secretAccessKey, string bucketName) GetS3Config()
    {
        var url = Environment.GetEnvironmentVariable("S3StorageOptions__ServiceUrl")!;
        var accessKeyId = Environment.GetEnvironmentVariable("S3StorageOptions__AccessKeyId")!;
        var secretAccessKey = Environment.GetEnvironmentVariable("S3StorageOptions__SecretAccessKey")!;
        var bucketName = Environment.GetEnvironmentVariable("S3StorageOptions__BucketName")!;
        return (url, accessKeyId, secretAccessKey, bucketName);
    }
}

