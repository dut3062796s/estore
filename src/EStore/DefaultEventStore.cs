﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ECommon.Logging;
using ECommon.Scheduling;
using ECommon.Storage;
using ECommon.Utilities;

namespace EStore
{
    public class DefaultEventStore : IEventStore
    {
        #region Private Variables

        private readonly object _lockObj = new object();
        private const char IndexSeparator = ';';
        private const char IndexContentSeparator = ':';
        private const int PersistIndexBatchSize = 1000;
        private ChunkManager _eventDataChunkManager;
        private ChunkWriter _eventDataChunkWriter;
        private ChunkReader _eventDataChunkReader;
        private ChunkManager _aggregateIndexChunkManager;
        private ChunkWriter _aggregateIndexChunkWriter;
        private ChunkReader _aggregateIndexChunkReader;
        private ChunkManager _commandIndexChunkManager;
        private ChunkWriter _commandIndexChunkWriter;
        private ChunkReader _commandIndexChunkReader;
        private CacheDict<string, CommandIndexData> _commandIndexCacheDict;
        private CacheDict<string, AggregateLatestVersionData> _aggregateIndexCacheDict;
        private ConcurrentQueue<EventStreamRecord> _eventStreamRecordQueue = new ConcurrentQueue<EventStreamRecord>();
        private ConcurrentQueue<EventStreamRecord> _swapedEventStreamRecordQueue = new ConcurrentQueue<EventStreamRecord>();
        private readonly ManualResetEvent _loadIndexWaitHandle = new ManualResetEvent(false);
        private readonly IScheduleService _scheduleService;
        private readonly ILogger _logger;
        private bool _isAggregateIndexLoaded = false;
        private bool _isCommandIndexLoaded = false;

        #endregion

        /// <summary>Represents the interval of the persist event index and command index task, the default value is 1 seconds.
        /// </summary>
        public int PersistIndexIntervalMilliseconds { get; set; }
        /// <summary>Represents the max cache count of commandIndex, the default value is 10000000.
        /// </summary>
        public int CommandIndexMaxCacheCount { get; set; }
        /// <summary>Represents the interval of removing commandIndex task, the default value is 10 seconds.
        /// </summary>
        public int RemoveExceedCommandIndexCacheIntervalSeconds { get; set; }
        /// <summary>Represents the max cache count of aggregateIndex, the default value is 10000000.
        /// </summary>
        public int AggregateIndexMaxCacheCount { get; set; }
        /// <summary>Represents the interval of removing aggregateIndex task, the default value is 10 seconds.
        /// </summary>
        public int RemoveExceedAggregateIndexCacheIntervalSeconds { get; set; }

        public DefaultEventStore(IScheduleService scheduleService, ILoggerFactory loggerFactory)
        {
            _scheduleService = scheduleService;
            PersistIndexIntervalMilliseconds = 1000;
            CommandIndexMaxCacheCount = 10000000;
            AggregateIndexMaxCacheCount = 10000000;
            RemoveExceedCommandIndexCacheIntervalSeconds = 10 * 1000;
            RemoveExceedAggregateIndexCacheIntervalSeconds = 10 * 1000;
            _logger = loggerFactory.Create(GetType().FullName);
            _commandIndexCacheDict = new CacheDict<string, CommandIndexData>("CommandIndex", _logger, CommandIndexMaxCacheCount);
            _aggregateIndexCacheDict = new CacheDict<string, AggregateLatestVersionData>("AggregateIndex", _logger, AggregateIndexMaxCacheCount);
        }

        public void Init(ChunkManagerConfig eventDataChunkConfig, ChunkManagerConfig aggregateIndexChunkConfig, ChunkManagerConfig commandIndexChunkConfig)
        {
            _eventDataChunkManager = new ChunkManager("EventDataChunk", eventDataChunkConfig, false);
            _eventDataChunkWriter = new ChunkWriter(_eventDataChunkManager);
            _eventDataChunkReader = new ChunkReader(_eventDataChunkManager, _eventDataChunkWriter);

            _aggregateIndexChunkManager = new ChunkManager("AggregateIndexChunk", aggregateIndexChunkConfig, false);
            _aggregateIndexChunkWriter = new ChunkWriter(_aggregateIndexChunkManager);
            _aggregateIndexChunkReader = new ChunkReader(_aggregateIndexChunkManager, _aggregateIndexChunkWriter);

            _commandIndexChunkManager = new ChunkManager("CommandIndexChunk", commandIndexChunkConfig, false);
            _commandIndexChunkWriter = new ChunkWriter(_commandIndexChunkManager);
            _commandIndexChunkReader = new ChunkReader(_commandIndexChunkManager, _commandIndexChunkWriter);

            _eventDataChunkManager.Load(ReadEventStreamRecord);
            _aggregateIndexChunkManager.Load(ReadIndexRecord);
            _commandIndexChunkManager.Load(ReadIndexRecord);

            _eventDataChunkWriter.Open();
            _aggregateIndexChunkWriter.Open();
            _commandIndexChunkWriter.Open();

            LoadIndexData();
        }
        public void Start()
        {
            _scheduleService.StartTask("PersistIndex", PersistIndex, PersistIndexIntervalMilliseconds, PersistIndexIntervalMilliseconds);
            _scheduleService.StartTask("RemoveExpiredCommandIndexes", RemoveExpiredCommandIndexes, RemoveExceedCommandIndexCacheIntervalSeconds, RemoveExceedCommandIndexCacheIntervalSeconds);
            _scheduleService.StartTask("RemoveExceedAggregates", RemoveExceedAggregateIndexes, RemoveExceedAggregateIndexCacheIntervalSeconds, RemoveExceedAggregateIndexCacheIntervalSeconds);
        }
        public void Stop()
        {
            PersistIndex();
            _scheduleService.StopTask("PersistIndex");
            _scheduleService.StopTask("RemoveExpiredCommandIndexes");
            _scheduleService.StopTask("RemoveExceedAggregates");
            _eventDataChunkWriter.Close();
            _aggregateIndexChunkWriter.Close();
            _commandIndexChunkWriter.Close();
        }
        public EventAppendResult AppendEventStreams(IEnumerable<IEventStream> eventStreams)
        {
            lock (_lockObj)
            {
                var eventStreamDict = GroupEventStreamByAggregateRoot(eventStreams);
                var eventAppendResult = new EventAppendResult();

                foreach (var entry in eventStreamDict)
                {
                    var aggregateRootId = entry.Key;
                    var eventStreamList = entry.Value;

                    //检查当前聚合根的事件是否合法
                    if (!CheckAggregateEvents(aggregateRootId, eventStreamList, eventAppendResult, out AggregateLatestVersionData aggregateLatestVersionData))
                    {
                        continue;
                    }

                    //如果合法，则持久化事件到文件
                    foreach (var eventStream in eventStreamList)
                    {
                        var record = new EventStreamRecord
                        {
                            AggregateRootId = eventStream.AggregateRootId,
                            AggregateRootType = eventStream.AggregateRootType,
                            Version = eventStream.Version,
                            CommandId = eventStream.CommandId,
                            Timestamp = eventStream.Timestamp,
                            CommandCreateTimestamp = eventStream.CommandCreateTimestamp,
                            Events = eventStream.Events
                        };

                        if (aggregateLatestVersionData != null)
                        {
                            record.PreviousRecordLogPosition = aggregateLatestVersionData.LogPosition;
                        }

                        //写入事件到文件
                        _eventDataChunkWriter.Write(record);
                         
                        //更新聚合根的内存缓存数据
                        RefreshMemoryCache(aggregateRootId, ref aggregateLatestVersionData, record);

                        //添加事件到事件队列，以便进行异步持久化事件索引和命令索引
                        _eventStreamRecordQueue.Enqueue(record);
                    }

                    eventAppendResult.SuccessAggregateRootIdList.Add(aggregateRootId);
                }

                return eventAppendResult;
            }
        }

        private void LoadIndexData()
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            _logger.InfoFormat("Start loading index data.");
            Task.Factory.StartNew(LoadAggregateIndexData);
            Task.Factory.StartNew(LoadCommandIndexData);
            _loadIndexWaitHandle.WaitOne();
            _logger.InfoFormat("Index data loading complete, totalTimeSpent: {0}s", stopWatch.Elapsed.TotalSeconds);
        }
        private void LoadAggregateIndexData()
        {
            var chunks = _aggregateIndexChunkManager.GetAllChunks();
            var stopWatch = new Stopwatch();
            var totalStopWatch = new Stopwatch();
            var aggregateIndexDict = new Dictionary<string, AggregateLatestVersionData>();
            totalStopWatch.Start();
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                if (aggregateIndexDict.Count >= AggregateIndexMaxCacheCount)
                {
                    break;
                }

                stopWatch.Restart();
                var chunk = chunks[i];
                var dataPosition = chunk.ChunkHeader.ChunkDataStartPosition;
                var recordQueue = new Queue<IndexRecord>();
                while (true)
                {
                    var record = _aggregateIndexChunkReader.TryReadAt(dataPosition, ReadIndexRecord, false);
                    if (record == null)
                    {
                        break;
                    }
                    if (string.IsNullOrEmpty(record.IndexInfo))
                    {
                        continue;
                    }
                    var indexArray = record.IndexInfo.Split(IndexSeparator);
                    foreach (var index in indexArray)
                    {
                        if (string.IsNullOrEmpty(index))
                        {
                            continue;
                        }
                        var itemArray = index.Split(IndexContentSeparator);
                        var aggregateRootId = itemArray[0];
                        if (aggregateIndexDict.ContainsKey(aggregateRootId))
                        {
                            aggregateIndexDict.Remove(aggregateRootId);
                        }
                        aggregateIndexDict.Add(aggregateRootId, new AggregateLatestVersionData(long.Parse(itemArray[2]), int.Parse(itemArray[1])));
                    }
                    dataPosition += record.RecordSize + sizeof(int) *2;
                }
                _logger.InfoFormat("AggregateIndexChunk read complete, chunkNumber: {0}, timeSpent: {1}s", chunk.ChunkHeader.ChunkNumber, stopWatch.Elapsed.TotalSeconds);
            }

            _logger.InfoFormat("AggregateIndex cache start loading, expectCount: {0}", aggregateIndexDict.Count);
            foreach (var entry in aggregateIndexDict)
            {
                _aggregateIndexCacheDict.AddOrUpdate(entry.Key, entry.Value);
                if (_aggregateIndexCacheDict.Count % 10000000 == 0)
                {
                    _logger.InfoFormat("AggregateIndex cache loadedCount: {0}, totalTimeSpent: {1}s", _aggregateIndexCacheDict.Count, totalStopWatch.Elapsed.TotalSeconds);
                }
            }

            //检查是否有漏掉的索引没有加载，TODO

            _logger.InfoFormat("AggregateIndex cache loaded complete, totalAggregateCount: {0}, totalTimeSpent: {1}s", _aggregateIndexCacheDict.Count, totalStopWatch.Elapsed.TotalSeconds);
            _isAggregateIndexLoaded = true;
            if (_isCommandIndexLoaded)
            {
                _loadIndexWaitHandle.Set();
            }
        }
        private void LoadCommandIndexData()
        {
            var chunks = _commandIndexChunkManager.GetAllChunks();
            var stopWatch = new Stopwatch();
            var totalStopWatch = new Stopwatch();
            totalStopWatch.Start();
            var commandIndexDict = new Dictionary<string, CommandIndexData>();
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                if (commandIndexDict.Count >= CommandIndexMaxCacheCount)
                {
                    break;
                }

                stopWatch.Restart();
                var chunk = chunks[i];
                var dataPosition = chunk.ChunkHeader.ChunkDataStartPosition;
                var recordQueue = new Queue<IndexRecord>();
                while (true)
                {
                    var record = _commandIndexChunkReader.TryReadAt(dataPosition, ReadIndexRecord, false);
                    if (record == null)
                    {
                        break;
                    }
                    if (string.IsNullOrEmpty(record.IndexInfo))
                    {
                        continue;
                    }
                    var indexArray = record.IndexInfo.Split(IndexSeparator);
                    foreach (var index in indexArray)
                    {
                        if (string.IsNullOrEmpty(index))
                        {
                            continue;
                        }
                        var array = index.Split(IndexContentSeparator);
                        if (array.Length != 3)
                        {
                            continue;
                        }
                        var commandId = array[0];
                        var commandCreateTimestamp = long.Parse(array[1]);
                        if (commandIndexDict.ContainsKey(commandId))
                        {
                            commandIndexDict.Remove(commandId);
                        }
                        commandIndexDict.Add(commandId, new CommandIndexData(commandCreateTimestamp));
                    }
                    dataPosition += record.RecordSize + sizeof(int) * 2;
                }
                _logger.InfoFormat("CommandIndexChunk read complete, chunkNumber: {0}, timeSpent: {1}", chunk.ChunkHeader.ChunkNumber, stopWatch.Elapsed.TotalSeconds);
            }

            _logger.InfoFormat("CommandIndex cache start loading, expectCount: {0}", commandIndexDict.Count);
            foreach (var entry in commandIndexDict)
            {
                _commandIndexCacheDict.AddOrUpdate(entry.Key, entry.Value);
                if (_commandIndexCacheDict.Count % 10000000 == 0)
                {
                    _logger.InfoFormat("CommandIndex cache loadedCount: {0}, totalTimeSpent: {1}", _commandIndexCacheDict.Count, totalStopWatch.Elapsed.TotalSeconds);
                }
            }

            //检查是否有漏掉的索引没有加载，TODO

            _logger.InfoFormat("CommandIndex cache loaded complete, totalCommandCount: {0}, totalTimeSpent: {1}", _commandIndexCacheDict.Count, totalStopWatch.Elapsed.TotalSeconds);
            _isCommandIndexLoaded = true;
            if (_isAggregateIndexLoaded)
            {
                _loadIndexWaitHandle.Set();
            }
        }
        private EventStreamRecord ReadEventStreamRecord(byte[] recordBuffer)
        {
            var record = new EventStreamRecord();
            record.ReadFrom(recordBuffer);
            return record;
        }
        private IndexRecord ReadIndexRecord(byte[] recordBuffer)
        {
            var record = new IndexRecord();
            record.ReadFrom(recordBuffer);
            return record;
        }
        private void PersistIndex()
        {
            lock (_lockObj)
            {
                var tmp = _eventStreamRecordQueue;
                _eventStreamRecordQueue = _swapedEventStreamRecordQueue;
                _swapedEventStreamRecordQueue = tmp;
            }

            var aggregateIndexBuilder = new StringBuilder();
            var commandIndexBuilder = new StringBuilder();
            var count = 0;
            while (_swapedEventStreamRecordQueue.TryDequeue(out EventStreamRecord eventStreamRecord))
            {
                aggregateIndexBuilder.Append(eventStreamRecord.AggregateRootId);
                aggregateIndexBuilder.Append(IndexContentSeparator);
                aggregateIndexBuilder.Append(eventStreamRecord.Version);
                aggregateIndexBuilder.Append(IndexContentSeparator);
                aggregateIndexBuilder.Append(eventStreamRecord.Timestamp);
                aggregateIndexBuilder.Append(IndexContentSeparator);
                aggregateIndexBuilder.Append(eventStreamRecord.LogPosition);
                aggregateIndexBuilder.Append(IndexSeparator);

                commandIndexBuilder.Append(eventStreamRecord.CommandId);
                commandIndexBuilder.Append(IndexContentSeparator);
                commandIndexBuilder.Append(eventStreamRecord.CommandCreateTimestamp);
                commandIndexBuilder.Append(IndexContentSeparator);
                commandIndexBuilder.Append(eventStreamRecord.LogPosition);
                commandIndexBuilder.Append(IndexSeparator);

                count++;

                if (count % PersistIndexBatchSize == 0)
                {
                    _aggregateIndexChunkWriter.Write(new IndexRecord
                    {
                        IndexInfo = aggregateIndexBuilder.ToString()
                    });
                    _commandIndexChunkWriter.Write(new IndexRecord
                    {
                        IndexInfo = commandIndexBuilder.ToString()
                    });
                    aggregateIndexBuilder.Clear();
                    commandIndexBuilder.Clear();
                }
            }
            if (aggregateIndexBuilder.Length > 0)
            {
                _aggregateIndexChunkWriter.Write(new IndexRecord
                {
                    IndexInfo = aggregateIndexBuilder.ToString()
                });
                aggregateIndexBuilder.Clear();
            }
            if (commandIndexBuilder.Length > 0)
            {
                _commandIndexChunkWriter.Write(new IndexRecord
                {
                    IndexInfo = commandIndexBuilder.ToString()
                });
                commandIndexBuilder.Clear();
            }
        }
        private void RemoveExpiredCommandIndexes()
        {
            _commandIndexCacheDict.RemoveExceedKeys();
        }
        private void RemoveExceedAggregateIndexes()
        {
            _aggregateIndexCacheDict.RemoveExceedKeys();
        }
        private IDictionary<string, IList<IEventStream>> GroupEventStreamByAggregateRoot(IEnumerable<IEventStream> eventStreams)
        {
            var eventStreamDict = new Dictionary<string, IList<IEventStream>>();
            var aggregateRootIdList = eventStreams.Select(x => x.AggregateRootId).Distinct().ToList();
            foreach (var aggregateRootId in aggregateRootIdList)
            {
                var eventStreamList = eventStreams.Where(x => x.AggregateRootId == aggregateRootId).ToList();
                if (eventStreamList.Count > 0)
                {
                    eventStreamDict.Add(aggregateRootId, eventStreamList);
                }
            }
            return eventStreamDict;
        }
        private bool CheckAggregateEvents(string aggregateRootId, IList<IEventStream> eventStreamList, EventAppendResult eventAppendResult, out AggregateLatestVersionData aggregateLatestVersionData)
        {
            aggregateLatestVersionData = null;

            //检查版本号是否连续
            if (!IsEventStreamVersionSequential(aggregateRootId, eventStreamList))
            {
                eventAppendResult.DuplicateEventAggregateRootIdList.Add(aggregateRootId);
                return false;
            }

            //检查命令是否重复
            var duplicatedCommandIdList = CheckDuplicatedCommands(eventStreamList);
            if (duplicatedCommandIdList.Count > 0)
            {
                eventAppendResult.DuplicateCommandAggregateRootIdList.Add(aggregateRootId, duplicatedCommandIdList);
                return false;
            }

            //检查版本号是否是基于当前聚合根最新的版本号而进行的修改
            if (!CheckAggregateRootLatestVersionMatch(aggregateRootId, eventStreamList.First().Version, out aggregateLatestVersionData))
            {
                eventAppendResult.DuplicateEventAggregateRootIdList.Add(aggregateRootId);
                return false;
            }

            return true;
        }
        private bool IsEventStreamVersionSequential(string aggregateRootId, IList<IEventStream> eventStreamList)
        {
            if (eventStreamList.Count <= 1)
            {
                return true;
            }
            for(var i = 0; i < eventStreamList.Count - 1; i++)
            {
                var version1 = eventStreamList[i].Version;
                var version2 = eventStreamList[i + 1].Version;
                if (version1 + 1 != version2)
                {
                    _logger.ErrorFormat("Aggregate event version sequential wrong，aggregateRootId: {0}, versionPrevious: {1}, versionNext: {2}",
                        aggregateRootId,
                        version1,
                        version2);
                    return false;
                }
            }
            return true;
        }
        private bool CheckAggregateRootLatestVersionMatch(string aggregateRootId, long firstInputEventVersion, out AggregateLatestVersionData aggregateLatestVersionData)
        {
            aggregateLatestVersionData = GetAggregateRootLatestVersionData(aggregateRootId);
            if (aggregateLatestVersionData != null)
            {
                if (aggregateLatestVersionData.Version + 1 != firstInputEventVersion)
                {
                    return false;
                }
            }
            else if (firstInputEventVersion != 1)
            {
                return false;
            }
            return true;
        }
        private AggregateLatestVersionData GetAggregateRootLatestVersionData(string aggregateRootId)
        {
            var existingAggregateLatestVersionData = _aggregateIndexCacheDict.Get(aggregateRootId);
            if (existingAggregateLatestVersionData != null)
            {
                return existingAggregateLatestVersionData;
            }
            var aggregateLatestVersionDataFromFile = LoadAggregateRootLatestVersionDataFromDisk(aggregateRootId);
            if (aggregateLatestVersionDataFromFile != null)
            {
                _aggregateIndexCacheDict.AddOrUpdate(aggregateRootId, aggregateLatestVersionDataFromFile);
            }
            return aggregateLatestVersionDataFromFile;
        }
        private AggregateLatestVersionData LoadAggregateRootLatestVersionDataFromDisk(string aggregateRootId)
        {
            //TODO
            return null;
        }
        private IList<string> CheckDuplicatedCommands(IList<IEventStream> eventStreamList)
        {
            var duplicatedCommandIdList = new List<string>();
            foreach(var eventStream in eventStreamList)
            {
                if (IsCommandDuplicated(eventStream))
                {
                    duplicatedCommandIdList.Add(eventStream.CommandId);
                }
            }
            return duplicatedCommandIdList;
        }
        private bool IsCommandDuplicated(IEventStream eventStream)
        {
            if (_commandIndexCacheDict.Exist(eventStream.CommandId))
            {
                return true;
            }
            else if (IsCommandExistOnDisk(eventStream))
            {
                return true;
            }
            return false;
        }
        private bool IsCommandExistOnDisk(IEventStream eventStream)
        {
            //TODO
            return false;
        }
        private void RefreshMemoryCache(string aggregateRootId, ref AggregateLatestVersionData aggregateLatestVersionData, EventStreamRecord record)
        {
            if (aggregateLatestVersionData != null)
            {
                var oldLastActiveTimestamp = aggregateLatestVersionData.LastActiveTimestamp;
                aggregateLatestVersionData.Update(record.LogPosition, record.Version);
                var newLastActiveTimestamp = aggregateLatestVersionData.LastActiveTimestamp;
                _aggregateIndexCacheDict.UpdateTimeKeyDict(aggregateRootId, oldLastActiveTimestamp, newLastActiveTimestamp);
            }
            else
            {
                aggregateLatestVersionData = new AggregateLatestVersionData(record.LogPosition, record.Version);
                _aggregateIndexCacheDict.AddOrUpdate(aggregateRootId, aggregateLatestVersionData);
            }

            _commandIndexCacheDict.AddOrUpdate(record.CommandId, new CommandIndexData(record.CommandCreateTimestamp));
        }

        class AggregateLatestVersionData : ITimeValue
        {
            public long LogPosition { get; private set; }
            public int Version { get; private set; }
            public long LastActiveTimestamp { get; private set; }

            public AggregateLatestVersionData(long logPosition, int version)
            {
                LogPosition = logPosition;
                Version = version;
                LastActiveTimestamp = DateTime.Now.Ticks;
            }

            public void Update(long logPosition, int version)
            {
                LogPosition = logPosition;
                Version = version;
                LastActiveTimestamp = DateTime.Now.Ticks;
            }
        }
        class CommandIndexData : ITimeValue
        {
            public long LastActiveTimestamp { get; private set; }

            public CommandIndexData(long commandTimestamp)
            {
                LastActiveTimestamp = commandTimestamp;
            }
        }
        class EventStreamRecord : ILogRecord
        {
            public long PreviousRecordLogPosition { get; set; }
            public long LogPosition { get; set; }
            public long RecordSize { get; set; }
            public string AggregateRootId { get; set; }
            public string AggregateRootType { get; set; }
            public int Version { get; set; }
            public string CommandId { get; set; }
            public long CommandCreateTimestamp { get; set; }
            public long Timestamp { get; set; }
            public string Events { get; set; }

            public void ReadFrom(byte[] recordBuffer)
            {
                var srcOffset = 0;

                LogPosition = ByteUtil.DecodeLong(recordBuffer, srcOffset, out srcOffset);
                PreviousRecordLogPosition = ByteUtil.DecodeLong(recordBuffer, srcOffset, out srcOffset);
                AggregateRootId = ByteUtil.DecodeString(recordBuffer, srcOffset, out srcOffset);
                AggregateRootType = ByteUtil.DecodeString(recordBuffer, srcOffset, out srcOffset);
                Version = ByteUtil.DecodeInt(recordBuffer, srcOffset, out srcOffset);
                CommandId = ByteUtil.DecodeString(recordBuffer, srcOffset, out srcOffset);
                CommandCreateTimestamp = ByteUtil.DecodeLong(recordBuffer, srcOffset, out srcOffset);
                Timestamp = ByteUtil.DecodeLong(recordBuffer, srcOffset, out srcOffset);
                Events = ByteUtil.DecodeString(recordBuffer, srcOffset, out srcOffset);

                RecordSize = recordBuffer.Length;
            }
            public void WriteTo(long logPosition, BinaryWriter writer)
            {
                LogPosition = logPosition;

                //LogPosition
                writer.Write(LogPosition);

                //PreviousRecordLogPosition
                writer.Write(PreviousRecordLogPosition);

                //AggregateRootId
                var aggregateRootIdBytes = Encoding.UTF8.GetBytes(AggregateRootId);
                writer.Write(aggregateRootIdBytes.Length);
                writer.Write(aggregateRootIdBytes);

                //AggregateRootType
                var aggregateRootTypeBytes = Encoding.UTF8.GetBytes(AggregateRootType);
                writer.Write(aggregateRootTypeBytes.Length);
                writer.Write(aggregateRootTypeBytes);

                //Version
                writer.Write(Version);

                //CommandId
                var commandIdBytes = Encoding.UTF8.GetBytes(CommandId);
                writer.Write(commandIdBytes.Length);
                writer.Write(commandIdBytes);

                //CommandCreateTimestamp
                writer.Write(CommandCreateTimestamp);

                //Timestamp
                writer.Write(Timestamp);

                //Events
                var eventsBytes = Encoding.UTF8.GetBytes(Events);
                writer.Write(eventsBytes.Length);
                writer.Write(eventsBytes);
            }
        }
        class IndexRecord : ILogRecord
        {
            public long RecordSize { get; set; }
            public string IndexInfo { get; set; }

            public void ReadFrom(byte[] recordBuffer)
            {
                var srcOffset = 0;
                IndexInfo = ByteUtil.DecodeString(recordBuffer, srcOffset, out srcOffset);
                RecordSize = recordBuffer.Length;
            }
            public void WriteTo(long logPosition, BinaryWriter writer)
            {
                var indexInfoBytes = Encoding.UTF8.GetBytes(IndexInfo);
                writer.Write(indexInfoBytes.Length);
                writer.Write(indexInfoBytes);
            }
        }
    }
}
