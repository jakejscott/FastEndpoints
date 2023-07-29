﻿using FastEndpoints.Messaging.Jobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace FastEndpoints;

internal abstract class JobQueueBase
{
    //key: tCommand
    //val: job queue for the command type
    //values get created when the DI container resolves each job queue type and the ctor is run.
    //see ctor in JobQueue<TCommand, TStorageRecord, TStorageProvider>
    protected static readonly ConcurrentDictionary<Type, JobQueueBase> _allQueues = new();

    protected abstract Task StoreJobAsync(object command, DateTime? executeAfter, DateTime? expireOn, CancellationToken ct);

    internal abstract void SetExecutionLimits(int concurrencyLimit, TimeSpan executionTimeLimit);

    internal static Task AddToQueueAsync(ICommand command, DateTime? executeAfter, DateTime? expireOn, CancellationToken ct)
    {
        var tCommand = command.GetType();

        if (!_allQueues.TryGetValue(tCommand, out var queue))
            throw new InvalidOperationException($"A job queue has not been registered for [{tCommand.FullName}]");

        return queue.StoreJobAsync(command, executeAfter, expireOn, ct);
    }
}

// created by DI as singleton
internal sealed class JobQueue<TCommand, TStorageRecord, TStorageProvider> : JobQueueBase
    where TCommand : ICommand
    where TStorageRecord : IJobStorageRecord, new()
    where TStorageProvider : IJobStorageProvider<TStorageRecord>
{
    private static readonly Type _tCommand = typeof(TCommand);
    private static readonly string _tCommandName = _tCommand.FullName!;
    private static readonly string _queueID = _tCommandName.ToHash();

    private readonly ParallelOptions _parallelOptions = new() { MaxDegreeOfParallelism = Environment.ProcessorCount };
    private readonly CancellationToken _appCancellation;
    private readonly TStorageProvider _storage;
    private readonly SemaphoreSlim _sem = new(0);
    private TimeSpan _executionTimeLimit = Timeout.InfiniteTimeSpan;
    private readonly ILogger _log;

    public JobQueue(TStorageProvider storageProvider, IHostApplicationLifetime appLife, ILogger<JobQueue<TCommand, TStorageRecord, TStorageProvider>> logger)
    {
        _allQueues[_tCommand] = this;
        _storage = storageProvider;
        _appCancellation = appLife.ApplicationStopping;
        _parallelOptions.CancellationToken = _appCancellation;
        _log = logger;
        JobStorage<TStorageRecord, TStorageProvider>.Provider = _storage;
        JobStorage<TStorageRecord, TStorageProvider>.AppCancellation = _appCancellation;
    }

    internal override void SetExecutionLimits(int concurrencyLimit, TimeSpan executionTimeLimit)
    {
        _parallelOptions.MaxDegreeOfParallelism = concurrencyLimit;
        _executionTimeLimit = executionTimeLimit;
        _ = CommandExecutorTask();
    }

    protected async override Task StoreJobAsync(object command, DateTime? executeAfter, DateTime? expireOn, CancellationToken ct)
    {
        await _storage.StoreJobAsync(new()
        {
            QueueID = _queueID,
            Command = command,
            ExecuteAfter = executeAfter ?? DateTime.UtcNow,
            ExpireOn = expireOn ?? DateTime.UtcNow.AddHours(4)
        }, ct);
        _sem.Release();
    }

    private async Task CommandExecutorTask()
    {
        var records = Enumerable.Empty<TStorageRecord>();
        var batchSize = _parallelOptions.MaxDegreeOfParallelism * 2;

        while (!_appCancellation.IsCancellationRequested)
        {
            try
            {
                records = await _storage.GetNextBatchAsync(new()
                {
                    Limit = batchSize,
                    QueueID = _queueID,
                    CancellationToken = _appCancellation,
                    Match = r => r.QueueID == _queueID &&
                                 !r.IsComplete &&
                                 DateTime.UtcNow >= r.ExecuteAfter &&
                                 DateTime.UtcNow <= r.ExpireOn
                });
            }
            catch (Exception x)
            {
                _log.StorageRetrieveError(_queueID, _tCommandName, x.Message);
                await Task.Delay(5000);
                continue;
            }

            if (!records.Any())
                await Task.WhenAny(_sem.WaitAsync(_appCancellation), Task.Delay(60000)); //query data again either in a minute or as soon as semaphore is released
            else
                await Parallel.ForEachAsync(records, _parallelOptions, ExecuteCommand);
        }

        async ValueTask ExecuteCommand(TStorageRecord record, CancellationToken _)
        {
            try
            {
                await ((TCommand)record.Command)
                    .ExecuteAsync(new CancellationTokenSource(_executionTimeLimit).Token);
            }
            catch (Exception x)
            {
                _log.CommandExecutionCritical(_tCommandName, x.Message);

                while (!_appCancellation.IsCancellationRequested)
                {
                    try
                    {
                        await _storage.OnHandlerExecutionFailureAsync(record, x, _appCancellation);
                        break;
                    }
                    catch (Exception xx)
                    {
                        _log.StorageOnExecutionFailureError(_queueID, _tCommandName, xx.Message);

#pragma warning disable CA2016
                        await Task.Delay(5000);
#pragma warning restore CA2016
                    }
                }

                return; //abort execution here
            }

            while (!_appCancellation.IsCancellationRequested)
            {
                try
                {
                    record.IsComplete = true;
                    await _storage.MarkJobAsCompleteAsync(record, _appCancellation);
                    break;
                }
                catch (Exception x)
                {
                    _log.StorageMarkAsCompleteError(_queueID, _tCommandName, x.Message);

#pragma warning disable CA2016
                    await Task.Delay(5000);
#pragma warning restore CA2016
                }
            }
        }
    }
}