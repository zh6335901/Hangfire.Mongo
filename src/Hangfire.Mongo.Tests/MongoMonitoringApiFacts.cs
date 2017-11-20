﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Hangfire.Common;
using Hangfire.Mongo.Database;
using Hangfire.Mongo.Dto;
using Hangfire.Mongo.PersistentJobQueue;
using Hangfire.Mongo.Tests.Utils;
using Hangfire.States;
using Hangfire.Storage;
using MongoDB.Bson;
using Moq;
using Xunit;

namespace Hangfire.Mongo.Tests
{
    [Collection("Database")]
    public class MongoMonitoringApiFacts
    {
        private const string DefaultQueue = "default";
        private const string FetchedStateName = "Fetched";
        private const int From = 0;
        private const int PerPage = 5;
        private readonly Mock<IPersistentJobQueue> _queue;
        private readonly Mock<IPersistentJobQueueProvider> _provider;
        private readonly Mock<IPersistentJobQueueMonitoringApi> _persistentJobQueueMonitoringApi;
        private readonly PersistentJobQueueProviderCollection _providers;

        public MongoMonitoringApiFacts()
        {
            _queue = new Mock<IPersistentJobQueue>();
            _persistentJobQueueMonitoringApi = new Mock<IPersistentJobQueueMonitoringApi>();

            _provider = new Mock<IPersistentJobQueueProvider>();
            _provider.Setup(x => x.GetJobQueue(It.IsNotNull<HangfireDbContext>())).Returns(_queue.Object);
            _provider.Setup(x => x.GetJobQueueMonitoringApi(It.IsNotNull<HangfireDbContext>()))
                .Returns(_persistentJobQueueMonitoringApi.Object);

            _providers = new PersistentJobQueueProviderCollection(_provider.Object);
        }

        [Fact, CleanDatabase]
        public void GetStatistics_ReturnsZero_WhenNoJobsExist()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var result = monitoringApi.GetStatistics();
                Assert.Equal(0, result.Enqueued);
                Assert.Equal(0, result.Failed);
                Assert.Equal(0, result.Processing);
                Assert.Equal(0, result.Scheduled);
            });
        }

        [Fact, CleanDatabase]
        public void GetStatistics_ReturnsExpectedCounts_WhenJobsExist()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                CreateJobInState(database, ObjectId.GenerateNewId(1), EnqueuedState.StateName);
                CreateJobInState(database, ObjectId.GenerateNewId(2), EnqueuedState.StateName);
                CreateJobInState(database, ObjectId.GenerateNewId(4), FailedState.StateName);
                CreateJobInState(database, ObjectId.GenerateNewId(5), ProcessingState.StateName);
                CreateJobInState(database, ObjectId.GenerateNewId(6), ScheduledState.StateName);
                CreateJobInState(database, ObjectId.GenerateNewId(7), ScheduledState.StateName);

                var result = monitoringApi.GetStatistics();
                Assert.Equal(2, result.Enqueued);
                Assert.Equal(1, result.Failed);
                Assert.Equal(1, result.Processing);
                Assert.Equal(2, result.Scheduled);
            });
        }

        [Fact, CleanDatabase]
        public void JobDetails_ReturnsNull_WhenThereIsNoSuchJob()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var result = monitoringApi.JobDetails(ObjectId.GenerateNewId().ToString());
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void JobDetails_ReturnsResult_WhenJobExists()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var job1 = CreateJobInState(database, ObjectId.GenerateNewId(1), EnqueuedState.StateName);

                var result = monitoringApi.JobDetails(job1.Id.ToString());

                Assert.NotNull(result);
                Assert.NotNull(result.Job);
                Assert.Equal("Arguments", result.Job.Args[0]);
                Assert.True(DateTime.UtcNow.AddMinutes(-1) < result.CreatedAt);
                Assert.True(result.CreatedAt < DateTime.UtcNow.AddMinutes(1));
            });
        }

        [Fact, CleanDatabase]
        public void EnqueuedJobs_ReturnsEmpty_WhenThereIsNoJobs()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var jobIds = new List<string>();

                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetEnqueuedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.EnqueuedJobs(DefaultQueue, From, PerPage);

                Assert.Empty(resultList);
            });
        }

        [Fact, CleanDatabase]
        public void EnqueuedJobs_ReturnsSingleJob_WhenOneJobExistsThatIsNotFetched()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var unfetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), EnqueuedState.StateName);

                var jobIds = new List<string> { unfetchedJob.Id.ToString() };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetEnqueuedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.EnqueuedJobs(DefaultQueue, From, PerPage);

                Assert.Equal(1, resultList.Count);
            });
        }

        [Fact, CleanDatabase]
        public void EnqueuedJobs_ReturnsEmpty_WhenOneJobExistsThatIsFetched()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var fetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), FetchedStateName);

                var jobIds = new List<string> { fetchedJob.Id.ToString() };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetEnqueuedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.EnqueuedJobs(DefaultQueue, From, PerPage);

                Assert.Empty(resultList);
            });
        }

        [Fact, CleanDatabase]
        public void EnqueuedJobs_ReturnsUnfetchedJobsOnly_WhenMultipleJobsExistsInFetchedAndUnfetchedStates()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var unfetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), EnqueuedState.StateName);
                var unfetchedJob2 = CreateJobInState(database, ObjectId.GenerateNewId(2), EnqueuedState.StateName);
                var fetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(3), FetchedStateName);

                var jobIds = new List<string>
                {
                    unfetchedJob.Id.ToString(),
                    unfetchedJob2.Id.ToString(),
                    fetchedJob.Id.ToString()
                };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetEnqueuedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.EnqueuedJobs(DefaultQueue, From, PerPage);

                Assert.Equal(2, resultList.Count);
            });
        }

        [Fact, CleanDatabase]
        public void FetchedJobs_ReturnsEmpty_WhenThereIsNoJobs()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var jobIds = new List<string>();

                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.FetchedJobs(DefaultQueue, From, PerPage);

                Assert.Empty(resultList);
            });
        }

        [Fact, CleanDatabase]
        public void FetchedJobs_ReturnsSingleJob_WhenOneJobExistsThatIsFetched()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var fetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), FetchedStateName);

                var jobIds = new List<string> { fetchedJob.Id.ToString() };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.FetchedJobs(DefaultQueue, From, PerPage);

                Assert.Equal(1, resultList.Count);
            });
        }

        [Fact, CleanDatabase]
        public void FetchedJobs_ReturnsEmpty_WhenOneJobExistsThatIsNotFetched()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var unfetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), EnqueuedState.StateName);

                var jobIds = new List<string> { unfetchedJob.Id.ToString() };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.FetchedJobs(DefaultQueue, From, PerPage);

                Assert.Empty(resultList);
            });
        }

        [Fact, CleanDatabase]
        public void FetchedJobs_ReturnsFetchedJobsOnly_WhenMultipleJobsExistsInFetchedAndUnfetchedStates()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var fetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(1), FetchedStateName);
                var fetchedJob2 = CreateJobInState(database, ObjectId.GenerateNewId(2), FetchedStateName);
                var unfetchedJob = CreateJobInState(database, ObjectId.GenerateNewId(3), EnqueuedState.StateName);

                var jobIds = new List<string>
                {
                    fetchedJob.Id.ToString(),
                    fetchedJob2.Id.ToString(),
                    unfetchedJob.Id.ToString()
                };
                _persistentJobQueueMonitoringApi.Setup(x => x
                    .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.FetchedJobs(DefaultQueue, From, PerPage);

                Assert.Equal(2, resultList.Count);
            });
        }

        [Fact, CleanDatabase]
        public void ProcessingJobs_ReturnsProcessingJobsOnly_WhenMultipleJobsExistsInProcessingSucceededAndEnqueuedState()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var processingJob = CreateJobInState(database, ObjectId.GenerateNewId(1), ProcessingState.StateName);

                var succeededJob = CreateJobInState(database, ObjectId.GenerateNewId(2), SucceededState.StateName, jobDto =>
                {
                    var processingState = new StateDto()
                    {
                        Name = ProcessingState.StateName,
                        Reason = null,
                        CreatedAt = DateTime.UtcNow,
                        Data = new Dictionary<string, string>
                        {
                            ["ServerId"] = Guid.NewGuid().ToString(),
                            ["StartedAt"] =
                            JobHelper.SerializeDateTime(DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(500)))
                        }
                    };
                    var succeededState = jobDto.StateHistory[0];
                    jobDto.StateHistory = new[] { processingState, succeededState };
                    return jobDto;
                });

                var enqueuedJob = CreateJobInState(database, ObjectId.GenerateNewId(3), EnqueuedState.StateName);

                var jobIds = new List<string>
                {
                    processingJob.Id.ToString(),
                    succeededJob.Id.ToString(),
                    enqueuedJob.Id.ToString()
                };
                _persistentJobQueueMonitoringApi.Setup(x => x
                        .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.ProcessingJobs(From, PerPage);

                Assert.Equal(1, resultList.Count);
            });
        }

        [Fact, CleanDatabase]
        public void FailedJobs_ReturnsFailedJobs_InDescendingOrder()
        {
            UseMonitoringApi((database, monitoringApi) =>
            {
                var failedJob0 = CreateJobInState(database, ObjectId.GenerateNewId(1), FailedState.StateName);
                var failedJob1 = CreateJobInState(database, ObjectId.GenerateNewId(2), FailedState.StateName);
                var failedJob2 = CreateJobInState(database, ObjectId.GenerateNewId(3), FailedState.StateName);
                

                var jobIds = new List<string>
                {
                    failedJob0.Id.ToString(),
                    failedJob1.Id.ToString(),
                    failedJob2.Id.ToString()
                };
                _persistentJobQueueMonitoringApi.Setup(x => x
                        .GetFetchedJobIds(DefaultQueue, From, PerPage))
                    .Returns(jobIds);

                var resultList = monitoringApi.FailedJobs(From, PerPage);
                
                Assert.Equal(failedJob0.Id.ToString(), resultList[2].Key);
                Assert.Equal(failedJob1.Id.ToString(), resultList[1].Key);
                Assert.Equal(failedJob2.Id.ToString(), resultList[0].Key);
            });
        }
        
        public static void SampleMethod(string arg)
        {
            Debug.WriteLine(arg);
        }

        private void UseMonitoringApi(Action<HangfireDbContext, MongoMonitoringApi> action)
        {
            var database = ConnectionUtils.CreateConnection();
            var monitoringApi = new MongoMonitoringApi(database, _providers);
            action(database, monitoringApi);
        }

        private JobDto CreateJobInState(HangfireDbContext database, ObjectId jobId, string stateName, Func<JobDto, JobDto> visitor = null)
        {
            var job = Job.FromExpression(() => SampleMethod("wrong"));

            Dictionary<string, string> stateData;
            if (stateName == EnqueuedState.StateName)
            {
                stateData = new Dictionary<string, string> { ["EnqueuedAt"] = $"{DateTime.UtcNow:o}" };
            }
            else if (stateName == ProcessingState.StateName)
            {
                stateData = new Dictionary<string, string>
                {
                    ["ServerId"] = Guid.NewGuid().ToString(),
                    ["StartedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(500)))
                };
            }
            else if (stateName == FailedState.StateName)
            {
                stateData = new Dictionary<string, string>
                {
                    ["ExceptionDetails"] = "Test_ExceptionDetails",
                    ["ExceptionMessage"] = "Test_ExceptionMessage",
                    ["ExceptionType"] = "Test_ExceptionType",
                    ["FailedAt"] = JobHelper.SerializeDateTime(DateTime.UtcNow.Subtract(TimeSpan.FromMilliseconds(10)))
                };
            }
            else
            {
                stateData = new Dictionary<string, string>();
            }

            var jobState = new StateDto()
            {
                Name = stateName,
                Reason = null,
                CreatedAt = DateTime.UtcNow,
                Data = stateData
            };

            var jobDto = new JobDto
            {
                Id = jobId,
                InvocationData = JobHelper.ToJson(InvocationData.Serialize(job)),
                Arguments = "[\"\\\"Arguments\\\"\"]",
                StateName = stateName,
                CreatedAt = DateTime.UtcNow,
                StateHistory = new[] { jobState }
            };
            if (visitor != null)
            {
                jobDto = visitor(jobDto);
            }
            database.Job.InsertOne(jobDto);

            var jobQueueDto = new JobQueueDto
            {
                FetchedAt = null,
                JobId = jobId,
                Queue = DefaultQueue
            };

            if (stateName == FetchedStateName)
            {
                jobQueueDto.FetchedAt = DateTime.UtcNow;
            }

            database.JobQueue.InsertOne(jobQueueDto);

            return jobDto;
        }
    }
}
