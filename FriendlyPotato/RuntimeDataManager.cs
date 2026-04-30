using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Timers;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MessagePack;
using Timer = System.Timers.Timer;

namespace FriendlyPotato;

public sealed class RuntimeDataManager : IDisposable
{
    private const string FileName = "data.dat";

    private readonly Data data;
    private readonly IPluginLog logger;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ReaderWriterLockSlim readerWriterLock = new(LockRecursionPolicy.NoRecursion);
    private volatile bool isDisposed = false;
    private ConcurrentQueue<(string PlayerName, bool Add, DateTime Timestamp)> pending = new();

    public RuntimeDataManager(IDalamudPluginInterface pi, IPluginLog pl)
    {
        pluginInterface = pi;
        logger = pl;
        if (!Directory.Exists(pi.GetPluginConfigDirectory()))
        {
            Directory.CreateDirectory(pi.GetPluginConfigDirectory());
            data = new Data();
        }
        else
        {
            data = FriendlyPotato.ReliableFileStorage.Exists(Path.Combine(pi.GetPluginConfigDirectory(), FileName))
                       ? MessagePackSerializer.Deserialize<Data>(
                           FriendlyPotato.ReliableFileStorage.ReadAllBytesAsync(Path.Combine(pi.GetPluginConfigDirectory(), FileName)).GetAwaiter().GetResult())
                       : new Data();
        }

        RunSerializer();
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        readerWriterLock.Dispose();
    }

    private async void RunSerializer()
    {
        while (!isDisposed)
        {
            try
            {
                var (serializedData, hasData) = SerializeData();
                if (!hasData) return;
                await FriendlyPotato.ReliableFileStorage.WriteAllBytesAsync(
                    Path.Combine(pluginInterface.GetPluginConfigDirectory(), FileName),
                    serializedData);
                logger.Debug("Saving runtime data complete");
            }
            catch (Exception ex)
            {
                logger.Error($"Error writing data to file: {ex.Message}");
            }

            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(60));
        }
    }

    private (byte[] Data, bool HasData) SerializeData()
    {
        if (readerWriterLock.TryEnterWriteLock(TimeSpan.FromMilliseconds(10)))
        {
            try
            {
                if (pending.IsEmpty) return ([], false);
                logger.Debug("Saving runtime data");

                while (pending.TryDequeue(out var item) && !isDisposed)
                {
                    if (item.Add || data.Seen.ContainsKey(item.PlayerName))
                    {
                        data.Seen[item.PlayerName] = item.Timestamp;
                    }
                }

                return (MessagePackSerializer.Serialize(data), true);
            }
            catch (Exception ex)
            {
                logger.Error($"Error saving data: {ex.Message}");
                throw;
            }
            finally
            {
                if (!isDisposed)
                    readerWriterLock.ExitWriteLock();
            }
        }
        else
        {
            throw new TimeoutException("Could not acquire write lock to save data.");
        }
    }

    public void MarkSeen(string playerName, bool add = true)
    {
        pending.Enqueue((playerName, add, DateTime.Now));
    }

    public DateTime? LastSeen(string playerName)
    {
        if (readerWriterLock.TryEnterReadLock(TimeSpan.FromMilliseconds(10)))
        {
            try
            {
                return data.Seen.TryGetValue(playerName, out var value) ? value : null;
            }
            finally
            {
                readerWriterLock.ExitReadLock();
            }
        }

        return null;
    }

    public bool TryGetLastSeen(string playerName, out DateTime lastSeen)
    {
        var dt = LastSeen(playerName);
        if (dt == null)
        {
            lastSeen = DateTime.Now;
            return false;
        }

        lastSeen = dt.Value;
        return true;
    }

    [MessagePackObject]
    public class Data
    {
        [Key(0)]
        public Dictionary<string, DateTime> Seen = new();
    }
}
