﻿using System;
using System.ComponentModel;
using System.IO;
using System.IO.IsolatedStorage;
using System.Net;
using System.Collections.Generic;
using System.Windows;
using System.Threading;
using System.Text;
using OneBusAway.WP7.ViewModel;
using System.Diagnostics;

namespace OneBusAway.WP7.Model
{
    /// <summary>
    /// A cache for HTTP GET requests, backed by IsolatedStorage.
    /// The full version of .NET includes support for this already, but Silverlight does not.
    /// </summary>
    public class HttpCache
    {
        /// <summary>
        /// Acquire a lock on this object prior to file IO.
        /// </summary>
        private object fileAccessSync = new object();

        private CacheMetadata metadata;
        private Timer reportingTimer;

        /// <summary>
        /// Allows multiple caches to coexist in storage.
        /// I.e. caches with different names are independent.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// The time in seconds for which a cached entry is good.
        /// </summary>
        public int ExpirationPeriod { get; private set; }

        /// <summary>
        /// The maximum number of entries in the cache.
        /// If the cache is full, old entries will be evicted to make room for new ones.
        /// </summary>
        public int Capacity { get; private set; }

        /// <summary>
        /// </summary>
        /// <param name="name">Identifier for the desired cache.  Caches with the same name target the same underlying storage.</param>
        /// <param name="expirationPeriod">Time in seconds for which a cached entry is good</param>
        /// <param name="capacity">Maximum number of entries in the cache</param>
        public HttpCache(string name, int expirationPeriod, int capacity)
        {
            this.Name = name;
            this.ExpirationPeriod = expirationPeriod;
            this.Capacity = capacity;
            this.metadata = new CacheMetadata(this);
            CacheCalls = 0;
            CacheHits = 0;
            CacheMisses = 0;
            CacheExpirations = 0;
            CacheEvictions = 0;

            reportingTimer = new Timer(new TimerCallback(CacheReportTrigger), null, new TimeSpan(0, 1, 0), new TimeSpan(0, 1, 0));
        }

        #region public methods

        public delegate void DownloadStringAsync_Completed(object sender, CacheDownloadStringCompletedEventArgs e);
        public void DownloadStringAsync(Uri address, DownloadStringAsync_Completed callback)
        {
            CacheCalls++;
            // lookup address in cache
            string cachedResult = CacheLookup(address);
            if (cachedResult != null)
            {
                CacheHits++;
                CacheDownloadStringCompletedEventArgs eventArgs = new CacheDownloadStringCompletedEventArgs(cachedResult);
                // Invoke on a different thread.  Otherwise we make the callback from the same thread as the
                // original call and wierd things could happen.
                Thread thread = new Thread(() => callback(this, eventArgs));
                thread.Start();
            }
            else
            {
                CacheMisses++;
                // not found, request data
#if WEBCLIENT
                WebClient client = new WebClient();
                client.DownloadStringCompleted += new CacheCallback(this, callback, address).Callback;
                client.DownloadStringAsync(address);
#else
                HttpWebRequest requestGetter = (HttpWebRequest)HttpWebRequest.Create(address);
                requestGetter.BeginGetResponse(
                    new AsyncCallback(new CacheCallback(this, callback, address).Callback),
                    requestGetter);
#endif
            }
        }

        /// <summary>
        /// Delete all data in the cache.
        /// </summary>
        public void Clear()
        {
            using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                if (iso.DirectoryExists(this.Name))
                {
                    lock (fileAccessSync)
                    {
                        // IsolatedStorage requires you to delete all the files before removing the directory
                        string[] files = iso.GetFileNames(this.Name + "\\*");
                        foreach (string file in files)
                        {
                            iso.DeleteFile(Path.Combine(this.Name, file));
                        }
                        iso.DeleteDirectory(this.Name);
                    }
                }
            }
            metadata.Clear();
        }

        /// <summary>
        /// Ensures that there is no entry for the given address in the cache.
        /// </summary>
        public void Invalidate(Uri address)
        {
            string fileName = MapAddressToFile(address);
            using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                if (iso.FileExists(fileName))
                {
                    lock (fileAccessSync)
                    {
                        iso.DeleteFile(fileName);
                    }
                }
            }
            metadata.RemoveUpdateTime(fileName);
        }

        #endregion

        #region diagnostic properties
        // Mainly useful for statistics of cache performance.

        public int CacheCalls { get; private set; }
        public int CacheHits { get; private set; }
        public int CacheMisses { get; private set; }
        public int CacheExpirations { get; private set; }
        public int CacheEvictions { get; private set; }

        private IDictionary<string, string> ReportCacheStats()
        {
            IDictionary<string, string> cacheStats = new Dictionary<string, string>();
            cacheStats.Add(string.Format("{0}-calls", Name), CacheCalls.ToString());
            cacheStats.Add(string.Format("{0}-hits", Name), CacheHits.ToString());
            cacheStats.Add(string.Format("{0}-misses", Name), CacheMisses.ToString());
            cacheStats.Add(string.Format("{0}-expirations", Name), CacheExpirations.ToString());
            cacheStats.Add(string.Format("{0}-evictions", Name), CacheEvictions.ToString());
            cacheStats.Add(string.Format("{0}-numberEntires", Name), metadata.GetNumberEntries().ToString());

            return cacheStats;
        }

        // This method will be called by the timer, and have an attribute attached
        // which will cause the analytics to call ReportCacheStats() and gather
        // the analytics
        private void CacheReportTrigger(object param)
        {
            
        }

        #endregion

        #region private helper methods

        /// <summary>
        /// Checks to see if we have a cached result for a given request
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        private string CacheLookup(Uri address)
        {
            using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                // get isolatedstorage for this cache
                if (iso.DirectoryExists(this.Name))
                {
                    // get result file for this address
                    string fileName = MapAddressToFile(address);
                    if (iso.FileExists(fileName))
                    {
                        lock (fileAccessSync)
                        {
                            if (CheckForExpiration(fileName))
                            {
                                return null;
                            }
                            // all good! return the content
                            using (IsolatedStorageFileStream stream = iso.OpenFile(fileName, FileMode.Open, FileAccess.Read))
                            {
                                using (StreamReader r = new StreamReader(stream))
                                {
                                    return r.ReadToEnd();
                                }
                            }
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Adds a new result for given request
        /// </summary>
        /// <param name="address"></param>
        /// <param name="result"></param>
        private void CacheAddResult(Uri address, string result)
        {
            using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication())
            {
                string fileName;
                lock (fileAccessSync)
                {
                    // get isolatedstorage for this cache
                    if (!iso.DirectoryExists(this.Name))
                    {
                        iso.CreateDirectory(this.Name);
                    }

                    fileName = MapAddressToFile(address);
                    EvictIfNecessary(iso);

                    using (IsolatedStorageFileStream stream = iso.OpenFile(fileName, FileMode.Create, FileAccess.Write))
                    {
                        using (StreamWriter writer = new StreamWriter(stream))
                        {
                            writer.Write(result);
                        }
                    }
                }
                UpdateExpiration(fileName);
            }
        }

        /// <summary>
        /// Evict an entry if we need to make room for a new one.
        /// </summary>
        /// <remarks>
        /// "A cache without an eviction policy is a memory leak"
        /// We might run into quota limits on IsolatedStorage.  We need to check those, and evict files to make room. 
        /// 
        /// Cache eviction policy is "least recently updated"
        /// Note this is not the same as least recently used.
        /// Rather, we're evicting the entry that will expire soonest.
        /// 
        /// This function assumes the caller has already locked fileAccessSync
        /// </remarks>
        /// <param name="iso"></param>
        private void EvictIfNecessary(IsolatedStorageFile iso)
        {
            if (metadata.GetNumberEntries() >= this.Capacity)
            {
                string[] filesInCache = iso.GetFileNames(this.Name + "\\*");

                // This should never happen, but if the file count doesn't match
                // go and clean up the cache
                if (filesInCache.Length > metadata.GetNumberEntries())
                {
                    Debug.Assert(false);

                    foreach (string filename in filesInCache)
                    {
                        // the GetFileNames call above does not return qualified paths, but we expect those for the rest of the calls
                        string qualifiedFilename = Path.Combine(this.Name, filename);
                        DateTime? updateTime = metadata.GetUpdateTime(qualifiedFilename);
                        if (null == updateTime)
                        {
                            // Then we have a file in the cache, but no record of it being put there... clean it up
                            // Most common way to hit this would be that I changed the internal naming format between versions.
                            iso.DeleteFile(qualifiedFilename);
                        }
                    }
                }

                KeyValuePair<string, DateTime> oldestFile = metadata.GetOldestFile();

                // If we are over capacity, there should always be at least one over-capacity file
                Debug.Assert(string.IsNullOrEmpty(oldestFile.Key) == false);

                if (string.IsNullOrEmpty(oldestFile.Key) == false)
                {
                    iso.DeleteFile(oldestFile.Key);

                    metadata.RemoveUpdateTime(oldestFile.Key);
                    CacheEvictions++;
                }
            }
        }

        /// <summary>
        /// Maps a request URI to the file name where we will store it in the cache
        /// </summary>
        /// <param name="address"></param>
        /// <returns></returns>
        private string MapAddressToFile(Uri address)
        {
            // HACK - This is specific to the OneBusAway calls.  Ideally, figure out a way to inject this logic so that the cache stays general purpose.
            // Remove the application key, because
            // 1. it's long, and will push us over the max path length
            // 2. it's constant, so no sense storing the information
            // 3. it's somewhat private
            string queryString = address.Query.Substring(1); // remove the leading ?
            string[] parameters = queryString.Split('&');

            string newQueryString = "?";
            bool first = true;
            foreach(string parameter in parameters)
            {
                if (!parameter.StartsWith("key="))
                {
                    if (!first)
                    {
                        newQueryString += "&";
                    }
                    else
                    {
                        first = false;
                    }
                    newQueryString += parameter;
                }
            }
            UriBuilder builder = new UriBuilder(address);
            builder.Query = newQueryString;
          
            string escaped = builder.Uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
            // tried Path.GetInvalidPathChars(). it doesn't seem to work here.
            escaped = escaped.Replace('/', '_');
            escaped = escaped.Replace(':', '_');
            escaped = escaped.Replace('?', '_');
            return Path.Combine(this.Name, escaped);
        }

        /// <summary>
        /// Checks if the specified file is expired.  If so, updates the cache accordingly.
        /// </summary>
        /// <remarks>
        /// Assumes that the caller has already locked fileAccessSync
        /// </remarks>
        /// <param name="fileName"></param>
        /// <returns>True if the cached file is expired</returns>
        private bool CheckForExpiration(string fileName)
        {
            if (metadata.IsExpired(fileName))
            {
                // purge the entry from the cache
                using (IsolatedStorageFile iso = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    iso.DeleteFile(fileName);
                }

                // and purge the metadata entry
                metadata.RemoveUpdateTime(fileName);

                CacheExpirations++;
                return true;
            }
            return false;
        }

        private void UpdateExpiration(string fileName)
        {
            metadata.AddUpdateTime(fileName, DateTime.Now);
        }

        /// <summary>
        /// Tracks metadata about a given file in the cache
        /// </summary>
        private class CacheMetadata
        {
            private object settingsLock = new object();
            private HttpCache owner;

            // Silverlight doesn't track file update / creation time.
            // This approach is based on http://msdn.microsoft.com/en-us/magazine/dd434650.aspx
            private Dictionary<string, DateTime> fileUpdateTimes;

            public CacheMetadata(HttpCache owner)
            {
                this.owner = owner;
                IsolatedStorageSettings cacheSettings = IsolatedStorageSettings.ApplicationSettings;
                if (cacheSettings.Contains(owner.Name))
                {
                    // load existing settings store for this cache
                    fileUpdateTimes = cacheSettings[owner.Name] as Dictionary<string, DateTime>;
                }
                else
                {
                    // create new settings store for this cache
                    fileUpdateTimes = new Dictionary<string, DateTime>();
                    cacheSettings[owner.Name] = fileUpdateTimes;
                }
            }

            public bool IsExpired(string filename)
            {
                if (fileUpdateTimes.ContainsKey(filename))
                {
                    DateTime lastGoodTime = fileUpdateTimes[filename].AddSeconds(owner.ExpirationPeriod);
                    return (lastGoodTime < DateTime.Now);
                }
                return true;
            }

            public DateTime? GetUpdateTime(string filename)
            {
                if (fileUpdateTimes.ContainsKey(filename))
                {
                    return fileUpdateTimes[filename];
                }
                return null;
            }

            public void AddUpdateTime(string filename, DateTime when)
            {
                fileUpdateTimes[filename] = when;
                // note this relies on referential integrity.
                // i.e. fileUpdateTimes is a reference to an object in the application settings
            }

            public void RemoveUpdateTime(string filename)
            {
                fileUpdateTimes.Remove(filename);
                // note this relies on referential integrity.
                // i.e. fileUpdateTimes is a reference to an object in the application settings
            }

            public int GetNumberEntries()
            {
                return fileUpdateTimes.Count;
            }

            public KeyValuePair<string, DateTime> GetOldestFile()
            {
                KeyValuePair<string, DateTime> oldestFile = new KeyValuePair<string, DateTime>(string.Empty, DateTime.MaxValue);

                foreach (KeyValuePair<string, DateTime> fileUpdateTime in fileUpdateTimes)
                {
                    if (fileUpdateTime.Value < oldestFile.Value)
                    {
                        oldestFile = fileUpdateTime;
                    }
                }

                return oldestFile;
            }

            public void Clear()
            {
                fileUpdateTimes.Clear();
                IsolatedStorageSettings cacheSettings = IsolatedStorageSettings.ApplicationSettings;
                cacheSettings.Remove(owner.Name);
            }
            
        }

        #endregion

        #region Callback support (event, event handler, etc)

        /// <summary>
        /// Exists solely to hold a reference to the originally requested URI
        /// </summary>
        private class CacheCallback
        {
            private Uri requestedAddress;
            private HttpCache owner;
            private DownloadStringAsync_Completed callback;

            public CacheCallback(HttpCache owner, DownloadStringAsync_Completed callback, Uri requestedAddress)
            {
                this.owner = owner;
                this.callback = callback;
                this.requestedAddress = requestedAddress;
            }

            public void Callback(object sender, DownloadStringCompletedEventArgs eventArgs)
            {
                // check for errors
                if (eventArgs.Cancelled)
                {
                    CacheDownloadStringCompletedEventArgs newArgs = CacheDownloadStringCompletedEventArgs.MakeCancelled();
                    callback(this, newArgs);
                }
                else if (eventArgs.Error != null)
                {
                    CacheDownloadStringCompletedEventArgs newArgs = new CacheDownloadStringCompletedEventArgs(eventArgs.Error);
                    callback(this, newArgs);
                }
                else
                {
                    // no errors -- add data to the cache
                    owner.CacheAddResult(requestedAddress, eventArgs.Result);
                    // and fire our event
                    CacheDownloadStringCompletedEventArgs newArgs = new CacheDownloadStringCompletedEventArgs(eventArgs.Result);
                    callback(this, newArgs);
                }
            }

            public void Callback(IAsyncResult asyncResult)
            {
                CacheDownloadStringCompletedEventArgs newArgs;

                try
                {
                    HttpWebRequest request = (HttpWebRequest)asyncResult.AsyncState;
                    HttpWebResponse response = (HttpWebResponse)request.EndGetResponse(asyncResult);

                    string statusDescr = response.StatusDescription;
                    long totalBytes = response.ContentLength;

                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        throw new WebserviceResponseException(response.StatusCode, request.RequestUri.ToString(), response.ToString(), null);
                    }

                    Stream s = response.GetResponseStream();
                    StreamReader sr = new StreamReader(s);
                    string results = sr.ReadToEnd();
                    // no errors -- add data to the cache
                    owner.CacheAddResult(requestedAddress, results);
                    
                    newArgs = new CacheDownloadStringCompletedEventArgs(results);
                }
                catch (Exception e)
                {
                    // TODO: Web exceptions will be caught here, and we just pass up
                    // that exception instead of recasting it to a WebserviceResponseException().
                    // This will result in the loss of the RequestUrl.
                    Debug.Assert(false);
                    newArgs = new CacheDownloadStringCompletedEventArgs(e);
                }

                callback(this, newArgs);
            }
        }


        // Yes, these mirror the ones defined in System.Net.
        // Those don't have public constructors, so they're not reusable.
        public class CacheDownloadStringCompletedEventArgs : AsyncCompletedEventArgs 
        {
            /// <summary>
            /// Indicates successful completion
            /// </summary>
            /// <param name="result"></param>
            public CacheDownloadStringCompletedEventArgs(string result) 
            {
                this.Result = result;
                this.Cancelled = false;
                this.Error = null;
            }
            /// <summary>
            /// Indicates an error was encountered
            /// </summary>
            /// <param name="error"></param>
            public CacheDownloadStringCompletedEventArgs(Exception error)
            {
                this.Result = null;
                this.Cancelled = false;
                this.Error = error;
            }
            /// <summary>
            /// Indicates the operation was cancelled
            /// </summary>
            /// <returns></returns>
            public static CacheDownloadStringCompletedEventArgs MakeCancelled()
            {
                CacheDownloadStringCompletedEventArgs rval = new CacheDownloadStringCompletedEventArgs("");
                rval.Result = null;
                rval.Cancelled = true;
                return rval;
            }
            public string Result { get; private set; }
            public new bool Cancelled { get; private set; }
            public new Exception Error { get; private set; }
        }

        #endregion
    }
}
