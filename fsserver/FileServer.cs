﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  public sealed class FileServer : Logging, IMediaServer, IVolatileMediaServer, IDisposable
  {
    private readonly static StringComparer icomparer = StringComparer.CurrentCultureIgnoreCase;

    private readonly Timer changeTimer = new Timer(TimeSpan.FromSeconds(20).TotalMilliseconds);

    private readonly Guid uuid = Guid.NewGuid();

    private readonly Timer watchTimer = new Timer(TimeSpan.FromMinutes(10).TotalMilliseconds);

    private readonly Regex re_sansitizeExt = new Regex(@"[^\w\d]+", RegexOptions.Compiled);

    private readonly DirectoryInfo[] directories;

    private readonly Identifiers ids;

    private DateTime lastChanged = DateTime.Now;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
    private FileStore store = null;

    private readonly DlnaMediaTypes types;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
    private readonly FileSystemWatcher[] watchers;


    public FileServer(DlnaMediaTypes types, Identifiers ids, params DirectoryInfo[] directories)
    {
      this.types = types;
      this.ids = ids;
      this.directories = directories.Distinct().ToArray();
      if (this.directories.Length == 0) {
        throw new ArgumentException(
          "Provide one or more directories",
          "directories"
          );
      }
      var parent = this.directories[0].Parent;
      if (parent == null) {
        parent = this.directories[0];
      }
      if (this.directories.Length == 1) {
        FriendlyName = string.Format(
          "{0} ({1})",
          this.directories[0].Name,
          parent.FullName
          );
      }
      else {
        FriendlyName = string.Format(
          "{0} ({1}) + {2}",
          this.directories[0].Name,
          parent.FullName,
          this.directories.Length - 1
          );
      }
      watchers = (from d in directories
                  select new FileSystemWatcher(d.FullName)).ToArray();
      uuid = DeriveUUID();
    }


    public event EventHandler Changed;
    public event EventHandler Changing;


    public string FriendlyName { get; set; }
    public Guid Uuid
    {
      get
      {
        return uuid;
      }
    }


    private Guid DeriveUUID()
    {
      var bytes = Guid.NewGuid().ToByteArray();
      var i = 0;
      var copy = Encoding.ASCII.GetBytes("sdlnafs");
      for (; i < copy.Length; ++i) {
        bytes[i] = copy[i];
      }
      copy = Encoding.UTF8.GetBytes(FriendlyName);
      for (var j = 0; j < copy.Length && i < bytes.Length - 1; ++i, ++j) {
        bytes[i] = copy[j];
      }
      return new Guid(bytes);
    }

    private void DoRoot()
    {
      lock (this) {
        IMediaFolder newMaster;
        if (directories.Length == 1) {
          newMaster = new PlainRootFolder(
            this,
            types,
            directories[0]
            );
        }
        else {
          var virtualMaster = new VirtualFolder(
            null,
            FriendlyName,
            Identifiers.GeneralRoot
            );
          foreach (var d in directories) {
            virtualMaster.Merge(
              new PlainRootFolder(this, types, d)
              );
          }
          newMaster = virtualMaster;
        }
        lock (ids) {
          ids.RegisterFolder(Identifiers.GeneralRoot, newMaster);
          ids.RegisterFolder(
            Identifiers.SamsungImages,
            new VirtualClonedFolder(
              newMaster,
              Identifiers.SamsungImages,
              types & DlnaMediaTypes.Image
              )
            );
          ids.RegisterFolder(
            Identifiers.SamsungAudio,
            new VirtualClonedFolder(
              newMaster,
              Identifiers.SamsungAudio,
              types & DlnaMediaTypes.Audio
              )
            );
          ids.RegisterFolder(
            Identifiers.SamsungVideo,
            new VirtualClonedFolder(
              newMaster,
              Identifiers.SamsungVideo,
              types & DlnaMediaTypes.Video
              )
            );
        }
      }

      Thumbnail();
    }

    private void OnChanged(Object source, FileSystemEventArgs e)
    {
      if (store != null && icomparer.Equals(e.FullPath, store.StoreFile.FullName)) {
        return;
      }
      DebugFormat("File System changed: {0}", e.FullPath);
      DelayedRescan(e.ChangeType);
    }

    private void OnRenamed(Object source, RenamedEventArgs e)
    {
      DebugFormat("File System changed (rename): {0}", e.FullPath);
      DelayedRescan(e.ChangeType);
    }

    private void RescanInternal()
    {
      Task.Factory.StartNew(() =>
      {
        if (Changing != null) {
          Changing.Invoke(this, EventArgs.Empty);
        }

        try {
          NoticeFormat("Rescanning {0}...", FriendlyName);
          DoRoot();
          NoticeFormat("Done rescanning {0}...", FriendlyName);
        }
        catch (Exception ex) {
          Error(ex);
        }


        if (Changed != null) {
          Changed.Invoke(this, EventArgs.Empty);
        }
      },
      TaskCreationOptions.AttachedToParent | TaskCreationOptions.LongRunning);
    }

    private void RescanTimer(object sender, ElapsedEventArgs e)
    {
      RescanInternal();
    }

    private void Thumbnail()
    {
      if (store == null) {
        return;
      }
      IEnumerable<WeakReference> items;
      lock (ids) {
        items = ids.Resources.ToList();
      }
      Thumbnailer.AddFiles(store, items);
    }


    internal void DelayedRescan(WatcherChangeTypes changeType)
    {
      if (changeTimer.Enabled) {
        return;
      }
      switch (changeType) {
        case WatcherChangeTypes.Deleted:
          changeTimer.Interval =
            TimeSpan.FromSeconds(2).TotalMilliseconds;
          break;
        case WatcherChangeTypes.Renamed:
          changeTimer.Interval =
            TimeSpan.FromSeconds(10).TotalMilliseconds;
          break;
        default:
          changeTimer.Interval =
            TimeSpan.FromSeconds(30).TotalMilliseconds;
          break;
      }
      var diff = DateTime.Now - lastChanged;
      if (diff.TotalSeconds <= 30) {
        // Avoid thrashing
        changeTimer.Interval = Math.Max(
          TimeSpan.FromSeconds(20).TotalMilliseconds,
          changeTimer.Interval
          );
        InfoFormat("Avoid thrashing {0}", changeTimer.Interval);
      }
      DebugFormat(
        "Change in {0} on {1}",
        changeTimer.Interval,
        FriendlyName
        );
      changeTimer.Enabled = true;
      lastChanged = DateTime.Now;
    }

    internal Cover GetCover(BaseFile file)
    {
      if (store != null) {
        return store.MaybeGetCover(file);
      }
      return null;
    }

    internal BaseFile GetFile(PlainFolder aParent, FileInfo info)
    {
      BaseFile item;
      lock (ids) {
        item = ids.GetItemByPath(info.FullName) as BaseFile;
      }
      if (item != null &&
        item.InfoDate == info.LastAccessTimeUtc &&
        item.InfoSize == info.Length) {
        return item;
      }

      var ext = re_sansitizeExt.Replace(
        info.Extension.ToLower().Substring(1),
        string.Empty
        );
      var type = DlnaMaps.Ext2Dlna[ext];
      var mediaType = DlnaMaps.Ext2Media[ext];

      if (store != null) {
        item = store.MaybeGetFile(this, info, type);
        if (item != null) {
          return item;
        }
      }

      return BaseFile.GetFile(aParent, info, type, mediaType);
    }

    internal void UpdateFileCache(BaseFile aFile)
    {
      if (store != null) {
        store.MaybeStoreFile(aFile);
      }
    }


    public void Dispose()
    {
      foreach (var w in watchers) {
        w.Dispose();
      }
      if (changeTimer != null) {
        changeTimer.Dispose();
      }
      if (watchTimer != null) {
        watchTimer.Dispose();
      }
      if (store != null) {
        store.Dispose();
      }
    }

    public IMediaItem GetItem(string id)
    {
      lock (ids) {
        return ids.GetItemById(id);
      }
    }

    public void Load()
    {
      if (types == DlnaMediaTypes.Audio && !ids.HasViews) {
        ids.AddView("music");
      }
      DoRoot();

      changeTimer.AutoReset = false;
      changeTimer.Elapsed += RescanTimer;

      foreach (var watcher in watchers) {
        watcher.IncludeSubdirectories = true;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
      }

      watchTimer.Elapsed += RescanTimer;
      watchTimer.Enabled = true;
    }

    public void Rescan()
    {
      RescanInternal();
    }

    public void SetCacheFile(FileInfo info)
    {
      if (store != null) {
        store.Dispose();
        store = null;
      }
      try {
        store = new FileStore(info);
      }
      catch (Exception ex) {
        Warn("FileStore is not availble; failed to load SQLite Adapter", ex);
        store = null;
      }
    }
  }
}
