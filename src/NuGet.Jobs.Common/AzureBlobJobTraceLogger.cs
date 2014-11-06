﻿using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.Jobs.Common
{
    /// <summary>
    /// This TraceListener helps log to Azure Blob Storage
    /// NOTE: For logging within this class which is rare, try using LogConsoleOnly method
    ///       such that any logging from within this class does not hit storage
    /// </summary>
    public sealed class AzureBlobJobTraceListener : JobTraceListener
    {
        // 'const's
        private const int MaxExpectedLogsPerRun = 1000000;
        public const int MaxLogBatchSize = 100;
        private const string JobLogNameFormat = "{0}/{1}.txt";
        private const string LogStorageContainerName = "ng-jobs-logs";
        private const string EndedLogName = "ended";

        // Static members
        private static ConcurrentQueue<ConcurrentQueue<string>> LogQueues;
        private static ConcurrentQueue<string> CurrentLogQueue;
        private static ConcurrentQueue<string> LocalOnlyLogQueue;
        private static int JobLogBatchCounter;
        private static int JobQueueBatchCounter;

        // For example, if MaxExpectedLogsPerRun is a million, and MaxLogBatchSize is 100, then maximum possible log batch counter would be 10000
        // That is a maximum of 5 digits. So, leadingzero specifier string would be "00000"
        // (Actually, 5 digits can handle upto 99999 which is roughly 10 times the expected. We are being, roughly, 10 times lenient)
        private static readonly string JobLogBatchCounterLeadingZeroSpecifier =
            new String('0',
                1 + Convert.ToInt32(
                        Math.Ceiling(
                            Math.Log10(
                                MaxExpectedLogsPerRun / MaxLogBatchSize))));

        // Instance members
        private CloudStorageAccount LogStorageAccount { get; set; }
        private CloudBlobContainer LogStorageContainer { get; set; }
        private string JobLogNamePrefix { get; set; }
        private string JobLocalLogFolderPath { get; set; }

        public AzureBlobJobTraceListener(string jobName) : base(jobName)
        {
            string nugetJobsLocalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LogStorageContainerName);
            Directory.CreateDirectory(nugetJobsLocalPath);
            Interlocked.Exchange(ref LogQueues, new ConcurrentQueue<ConcurrentQueue<string>>());
            Interlocked.Exchange(ref CurrentLogQueue, new ConcurrentQueue<string>());
            Interlocked.Exchange(ref JobLogBatchCounter, 0);
            Interlocked.Exchange(ref JobQueueBatchCounter, 0);
            Interlocked.Exchange(ref LocalOnlyLogQueue, new ConcurrentQueue<string>());

            var cstr = Environment.GetEnvironmentVariable(EnvironmentVariableKeys.StoragePrimary);
            if(cstr == null)
            {
                throw new ArgumentException("Storage Primary environment variable needs to be set to use Azure Storage for logging. Try passing '-consoleLogOnly' for avoiding this error");
            }

            LogStorageAccount = CloudStorageAccount.Parse(cstr);
            LogStorageContainer = LogStorageAccount.CreateCloudBlobClient().GetContainerReference(LogStorageContainerName);
            LogStorageContainer.CreateIfNotExists();

            var dt = DateTime.UtcNow;
            JobLogNamePrefix = String.Format("{0}/{1}/{2}", jobName, dt.ToString("yyyy/MM/dd/HH/mm/ss/fffffff"), Environment.MachineName);
            JobLocalLogFolderPath = Path.Combine(nugetJobsLocalPath, String.Format("{0}-{1}", jobName, dt.ToString("yyyy-MM-dd-HH-mm-ss-fffffff")));
            Directory.CreateDirectory(JobLocalLogFolderPath);
            Task.Run(() => FlushRunner());
            Task.Run(() => LocalLogFlushRunner());
        }

        protected override void Log(TraceEventType traceEventType, string message)
        {
            LogConsoleOnly(traceEventType, message);
            QueueLog(
                MessageWithTraceEventType(
                    traceEventType,
                    message));
        }

        protected override void Log(TraceEventType traceEventType, string format, params object[] args)
        {
            var message = String.Format(format, args);
            Log(traceEventType, message);
        }

        private void QueueLog(string messageWithTraceLevel)
        {
            if(CurrentLogQueue == null)
            {
                // Consider using IDisposable pattern
                throw new ArgumentException("CurrentLogQueue cannot be null. It is likely that FlushAll has been called");
            }

            // FlushAll should never get called until after all the logging is done
            // This method is not thread-safe. If there was multi threading, CurrentLogQueue even after this point could be null
            // Let NullReferenceException be thrown, if this happens
            if(CurrentLogQueue.Count >= MaxLogBatchSize)
            {
                FlushCurrentQueue();
            }

            LocalOnlyLogQueue.Enqueue(messageWithTraceLevel);
            CurrentLogQueue.Enqueue(messageWithTraceLevel);
        }

        private void FlushRunner()
        {
            Thread.Sleep(1000);
            while (CurrentLogQueue != null)
            {
                Flush(skipCurrentBatch: true);
                Thread.Sleep(1000);
            }

            // Flush anything that may be pending due to timing
            Flush(skipCurrentBatch: true);

            // The following indicates that all the log flushing is done
            Interlocked.Exchange(ref JobLogBatchCounter, -1);
        }

        private void LocalLogFlushRunner()
        {
            while (LocalOnlyLogQueue != null)
            {
                LocalLogFlush();
            }
        }

        void LocalLogFlush()
        {
            try
            {
                using (var writer = File.AppendText(GetJobLocalLogPath(JobQueueBatchCounter)))
                {
                    string content;
                    while (LocalOnlyLogQueue != null && LocalOnlyLogQueue.TryDequeue(out content))
                    {
                        writer.WriteLine(content);
                        writer.Flush();
                    }
                }
            }
            catch (Exception)
            {
                // DO NOTHING
            }
        }

        private void FlushCurrentQueue()
        {
            LogConsoleOnly(TraceEventType.Verbose, "Creating a new concurrent log queue...");
            LogQueues.Enqueue(Interlocked.Exchange(ref CurrentLogQueue, new ConcurrentQueue<string>()));
            Interlocked.Increment(ref JobQueueBatchCounter);
        }

        public override void Flush(bool skipCurrentBatch = false)
        {
            try
            {
                if(!skipCurrentBatch)
                {
                    FlushCurrentQueue();
                }

                ConcurrentQueue<string> headQueue;
                while (LogQueues.TryDequeue(out headQueue))
                {
                    string blobName = String.Format(JobLogNameFormat, JobLogNamePrefix, JobLogBatchCounter.ToString(JobLogBatchCounterLeadingZeroSpecifier));
                    Save(blobName, headQueue);
                    Interlocked.Increment(ref JobLogBatchCounter);
                }
            }
            catch (Exception ex)
            {
                LogConsoleOnly(TraceEventType.Error, ex.ToString());
            }
        }

        public override void FlushAllAndEnd(string jobEndMessage)
        {
            base.FlushAllAndEnd(jobEndMessage);
            var logQueue = Interlocked.Exchange(ref CurrentLogQueue, null);
            if(logQueue != null)
            {
                LogQueues.Enqueue(logQueue);
            }

            while (JobLogBatchCounter != -1)
            {
                // Don't use the other overload of base.Log with format and args. That will call back into this class
                LogConsoleOnly(TraceEventType.Information, "Waiting for log flush runner loop to terminate...");
                Thread.Sleep(2000);
            }

            if(!LogQueues.IsEmpty)
            {
                throw new ArgumentException("LogQueues should be empty at the end of FlushAll...");
            }

            Interlocked.Exchange(ref LogQueues, null);
            var endedLogBlobName = String.Format(JobLogNameFormat, JobLogNamePrefix, EndedLogName);
            Save(endedLogBlobName, jobEndMessage);

            while(!LocalOnlyLogQueue.IsEmpty)
            {
                // DO NOTHING
                LogConsoleOnly(TraceEventType.Information, "Waiting for local logging flush runner loop to terminate...");
                Thread.Sleep(2000);
            }
            LogConsoleOnly(TraceEventType.Information, "Setting Local log only queue to null");
            LocalOnlyLogQueue = null;

            // At this point, the logs are all uploaded to azure and the current job run is done. Delete them
            if(Directory.Exists(JobLocalLogFolderPath))
            {
                LogConsoleOnly(TraceEventType.Information, "Deleting local log folder " + JobLocalLogFolderPath);
                Directory.Delete(JobLocalLogFolderPath, recursive: true);
            }
            LogConsoleOnly(TraceEventType.Information, "Successfully completed flushing of logs");
        }

        private void Save(string blobName, ConcurrentQueue<string> eventMessages)
        {
            StringBuilder builder = new StringBuilder();
            foreach(string eventMessage in eventMessages)
            {
                builder.AppendLine(eventMessage);
            }

            Save(blobName, builder.ToString());
        }

        private void Save(string blobName, string content)
        {
            // Don't use the other overload of base.Log with format and args. That will call back into this class
            var blob = LogStorageContainer.GetBlockBlobReference(blobName);
            LogConsoleOnly(TraceEventType.Verbose, "Uploading to " + blob.Uri.ToString());
            blob.UploadText(content);
        }

        private string GetJobLocalLogPath(int jobLogBatchCounter)
        {
            return Path.Combine(JobLocalLogFolderPath, String.Format("{0}.txt", jobLogBatchCounter));
        }
    }
}
