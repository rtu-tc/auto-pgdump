using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using CliWrap;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    });

var app = builder.Build();
var schedulerFactory = app.Services.GetRequiredService<ISchedulerFactory>();
var scheduler = await schedulerFactory.GetScheduler();

var job = JobBuilder.Create<PgDumpJob>()
    .WithIdentity("pgDumpJob", "default")
    .Build();
var trigger = TriggerBuilder.Create()
    .WithIdentity("cronTrigger", "default")
    .StartNow()
    .WithCronSchedule(Environment.GetEnvironmentVariable("ScheduleCron")!)
    .Build();

await scheduler.ScheduleJob(job, trigger);

await app.RunAsync();

[DisallowConcurrentExecution]
class PgDumpJob(ILogger<PgDumpJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        await CreateDump(context.CancellationToken);
        var (url, accessKeyId, secretAccessKey, bucketName, pathPrefix) = GetS3Config();
        using var s3Client = new AmazonS3Client(accessKeyId, secretAccessKey, new AmazonS3Config()
        {
            ServiceURL = url,
            ForcePathStyle = true
        });
        var key = $"{pathPrefix.TrimEnd('/')}/dump_{TimeProvider.System.GetUtcNow() + TimeSpan.FromHours(3):u}.backup";
        await PutObject(key, bucketName, s3Client, context.CancellationToken);
        await CopyObject(key, s3Client, bucketName, pathPrefix, context.CancellationToken);
    }

    private async Task CopyObject(string key, AmazonS3Client s3Client, string bucketName, string pathPrefix, CancellationToken cancellationToken)
    {
        logger.LogInformation("Requested to copy file {Key} as latest.backup", key);
        var targetKey = $"{pathPrefix.TrimEnd('/')}/latest.backup";
        // Create a list to store the upload part responses.
        var copyResponses = new List<CopyPartResponse>();

        // Setup information required to initiate the multipart upload.
        var initiateRequest = new InitiateMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = targetKey
        };

        // Initiate the upload.
        var initResponse = await s3Client.InitiateMultipartUploadAsync(initiateRequest);

        // Save the upload ID.
        var uploadId = initResponse.UploadId;
        // Get the size of the object.
        var metadataRequest = new GetObjectMetadataRequest
        {
            BucketName = bucketName,
            Key = key
        };

        var metadataResponse = await s3Client.GetObjectMetadataAsync(metadataRequest, cancellationToken: cancellationToken);
        var objectSize = metadataResponse.ContentLength; // Length in bytes.

        // Copy the parts.
        var partSize = 5 * (long)Math.Pow(2, 20); // Part size is 5 MB.

        var bytePosition = 0l;
        for (var i = 1; bytePosition < objectSize; i++)
        {
            var copyRequest = new CopyPartRequest
            {
                DestinationBucket = bucketName,
                DestinationKey = targetKey,
                SourceBucket = bucketName,
                SourceKey = key,
                UploadId = uploadId,
                FirstByte = bytePosition,
                LastByte = bytePosition + partSize - 1 >= objectSize ? objectSize - 1 : bytePosition + partSize - 1,
                PartNumber = i
            };

            copyResponses.Add(await s3Client.CopyPartAsync(copyRequest));

            bytePosition += partSize;
        }

        // Set up to complete the copy.
        var completeRequest =
        new CompleteMultipartUploadRequest
        {
            BucketName = bucketName,
            Key = targetKey,
            UploadId = initResponse.UploadId
        };
        completeRequest.AddPartETags(copyResponses);

        // Complete the copy.
        var completeUploadResponse =
            await s3Client.CompleteMultipartUploadAsync(completeRequest, cancellationToken: cancellationToken);
        logger.LogInformation("Job finished successfully");
    }

    private async Task PutObject(string key, string bucketName, AmazonS3Client s3Client, CancellationToken cancellationToken)
    {
        logger.LogInformation("Uploading file {Key} to s3 bucket {BucketName}", key, bucketName);
        var fileTransferUtility = new TransferUtility(s3Client);
        await fileTransferUtility.UploadAsync("dump.sql", bucketName, key, cancellationToken: cancellationToken);
        logger.LogInformation("File {Key} uploaded successfully to s3 bucket {BucketName}", key, bucketName);
    }

    private async Task CreateDump(CancellationToken cancellationToken)
    {
        var (host, port, dbname, user, password) = ParsePostgresConnectionString();
        logger.LogInformation("Started pg_dump of database {DatabaseTitle}", dbname);
        var args = (new string[] { "--host", host, "--port", port, "--dbname", dbname, "-U", user, "-f", "dump.sql" }).Concat(PgDumpExtraArgs.Split(' '));
        var response = await Cli.Wrap("pg_dump")
            .WithArguments(args.Where(a => !string.IsNullOrEmpty(a)))
            .WithEnvironmentVariables(new Dictionary<string, string?>() { { "PGPASSWORD", password } })
            .WithStandardErrorPipe(PipeTarget.ToDelegate(s => logger.LogError(s)))
            .ExecuteAsync(cancellationToken: cancellationToken);
        logger.LogInformation("pg_dump of database {DatabaseTitle} finished successfully", dbname);
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
    (string url, string accessKeyId, string secretAccessKey, string bucketName, string pathPrefix) GetS3Config()
    {
        var url = GetRequiredEnviromentVariable("S3StorageOptions__ServiceUrl");
        var accessKeyId = GetRequiredEnviromentVariable("S3StorageOptions__AccessKeyId");
        var secretAccessKey = GetRequiredEnviromentVariable("S3StorageOptions__SecretAccessKey");
        var bucketName = GetRequiredEnviromentVariable("S3StorageOptions__BucketName");
        var pathPrefix = GetRequiredEnviromentVariable("S3StorageOptions__PathPrefix");
        return (url, accessKeyId, secretAccessKey, bucketName, pathPrefix);
    }

    private static string PgDumpExtraArgs => Environment.GetEnvironmentVariable("PgDumpOptions__ExtraArgs") ?? "";
    private static string GetRequiredEnviromentVariable(string variable)
        => Environment.GetEnvironmentVariable(variable)
            ?? throw new InvalidDataException($"Environment variable \"{variable}\" not defined ");
}

