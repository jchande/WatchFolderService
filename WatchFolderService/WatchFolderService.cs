﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Net;

namespace WatchFolderService
{
    public partial class WatchFolderService : ServiceBase
    {
        private static long DEFAULT_PARTSIZE = 1048576;
        private static Object INFOFILE_LOCK = new Object(); // Lock for InfoFile that contains sync information
        private static bool SELF_SIGNED = true; // Target server is a self-signed server
        private static bool INITIALIZED = false;

        // Upload required information
        private string server = null;
        private string infoFilePath = null;
        private string watchFolder = null;
        private string userID = null;
        private string userKey = null;
        private string folderID = null;
        private int elapse = 10000;

        // Event log
        private System.Diagnostics.EventLog eventLog1;
        private static int EVENT_ID = 1;

        public WatchFolderService()
        {
            InitializeComponent();
            this.AutoLog = false; // Auto Event Log

            server = ConfigurationManager.AppSettings["Server"];
            infoFilePath = ConfigurationManager.AppSettings["InfoFilePath"];
            watchFolder = ConfigurationManager.AppSettings["WatchFolder"];
            userID = ConfigurationManager.AppSettings["UserID"];
            userKey = ConfigurationManager.AppSettings["UserKey"];
            folderID = ConfigurationManager.AppSettings["FolderID"];

            Common.SetServer(server);

            if (SELF_SIGNED)
            {
                // For self-signed servers
                EnsureCertificateValidation();
            }

            // Event Log
            eventLog1 = new System.Diagnostics.EventLog();
            if (!System.Diagnostics.EventLog.SourceExists("PanoptoWatchFolderService"))
            {
                System.Diagnostics.EventLog.CreateEventSource(
                    "PanoptoWatchFolderService", "PanoptoWatchFolderServiceLog");
            }

            eventLog1.Source = "PanoptoWatchFolderService";
            eventLog1.Log = "PanoptoWatchFolderServiceLog";
        }

        /// <summary>
        /// Start of service
        /// </summary>
        /// <param name="args">Arguments</param>
        protected override void OnStart(string[] args)
        {
            // Update the service state to Start Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_START_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            eventLog1.WriteEntry("Service Started Successfully"); // Event Log Record

            System.Timers.Timer timer = new System.Timers.Timer();
            timer.Interval = elapse;
            timer.Elapsed += new System.Timers.ElapsedEventHandler(OnTimer);
            timer.Start();

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_RUNNING;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        /// <summary>
        /// End function of service
        /// </summary>
        protected override void OnStop()
        {
            // Update the service state to Start Pending.
            ServiceStatus serviceStatus = new ServiceStatus();
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOP_PENDING;
            serviceStatus.dwWaitHint = 100000;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);

            eventLog1.WriteEntry("Service Stopped Successfully"); // Event Log Record
            eventLog1.Close();

            // Update the service state to Running.
            serviceStatus.dwCurrentState = ServiceState.SERVICE_STOPPED;
            SetServiceStatus(this.ServiceHandle, ref serviceStatus);
        }

        /// <summary>
        /// Service event that syncs the folder that happens on given interval
        /// </summary>
        /// <param name="sender">Timer object</param>
        /// <param name="args">Arguments</param>
        private void OnTimer(object sender, System.Timers.ElapsedEventArgs args)
        {
            lock (INFOFILE_LOCK)
            {
                DirectoryInfo di = new DirectoryInfo(Path.GetFullPath(watchFolder));
                Dictionary<string, DateTime> folderInfo = GetFolderInfo();
                Dictionary<string, DateTime> uploadFiles = new Dictionary<string, DateTime>();

                // Check files in folder for sync
                foreach (FileInfo fi in di.GetFiles("*.mp4"))
                {
                    bool inSync = false;
                    bool found = false;

                    if (folderInfo.ContainsKey(fi.Name))
                    {
                        found = true;
                        DateTime stored = folderInfo[fi.Name];
                        DateTime current = fi.LastWriteTime;
                        current = new DateTime(current.Ticks - (current.Ticks % TimeSpan.TicksPerSecond), current.Kind);

                        if (stored.Equals(current))
                        {
                            inSync = true;
                        }
                    }

                    // Add file to sync queue
                    if (!inSync)
                    {
                        if (found)
                        {
                            uploadFiles.Add(fi.FullName, folderInfo[fi.Name]);
                            folderInfo[fi.Name] = fi.LastWriteTime;
                        }
                        else
                        {
                            uploadFiles.Add(fi.FullName, DateTime.MinValue);
                            folderInfo.Add(fi.Name, fi.LastWriteTime);
                        }
                    }
                }

                ProcessUpload(uploadFiles, folderInfo);

                SetFolderInfo(folderInfo);

                EVENT_ID++;
            }
        }

        /// <summary>
        /// Uploads files in uploadFiles, revert LastWriteTime contained in folderInfo if file upload fails
        /// </summary>
        /// <param name="uploadFiles">Files to be uploaded</param>
        /// <param name="folderInfo">Information about file and its last write time</param>
        private void ProcessUpload(Dictionary<string, DateTime> uploadFiles, Dictionary<string, DateTime> folderInfo)
        {
            foreach (string filePath in uploadFiles.Keys)
            {
                try
                {
                    UploadAPIWrapper.UploadFile(userID,
                                                userKey, 
                                                folderID, 
                                                Path.GetFileName(filePath), 
                                                filePath, 
                                                DEFAULT_PARTSIZE);
                }
                catch (Exception ex)
                {
                    // Event Log Record
                    eventLog1.WriteEntry("Uploading " + filePath + " Failed: " + ex.Message, 
                                         EventLogEntryType.Error, 
                                         EVENT_ID); 
                    eventLog1.WriteEntry("Error Log: " + ex.StackTrace, EventLogEntryType.Error, EVENT_ID);

                    folderInfo[Path.GetFileName(filePath)] = uploadFiles[filePath];
                }
            }
        }

        /// <summary>
        /// Generate a Dictionary containing files in the folder and their last write time
        /// </summary>
        /// <returns>Dictionary containing files in folder and their last write time</returns>
        private Dictionary<string, DateTime> GetFolderInfo()
        {
            Dictionary<string, DateTime> folderInfo = new Dictionary<string, DateTime>();

            if (!File.Exists(Path.GetFullPath(infoFilePath)))
            {
                return folderInfo;
            }

            string[] infoFileLines = File.ReadAllLines(Path.GetFullPath(infoFilePath));

            foreach (string line in infoFileLines)
            {
                string[] info = line.Split(';');
                if (info.Length != 2)
                {
                    continue;
                }

                DateTime writeTime = GetDateTime(info[1]);

                folderInfo.Add(info[0], writeTime);
            }

            return folderInfo;
        }

        /// <summary>
        /// Store into InfoFile the files and last write time contained in parameter info
        /// </summary>
        /// <param name="info">Dictionary that stores files and their last write time</param>
        private void SetFolderInfo(Dictionary<string, DateTime> info)
        {
            using (System.IO.StreamWriter infoFile = new System.IO.StreamWriter(Path.GetFullPath(infoFilePath), false))
            {
                foreach (string fileName in info.Keys)
                {
                    string line = fileName + ";" + info[fileName].ToString("G");

                    infoFile.WriteLine(line);
                }
            }
        }

        /// <summary>
        /// Generate a DateTime object from given string
        /// </summary>
        /// <param name="timeString">String of date and time</param>
        /// <returns>DateTime created from timeString</returns>
        private DateTime GetDateTime(string timeString)
        {
            string[] timeInfo = timeString.Split(' ');
            string[] dateString = timeInfo[0].Split('/');
            string[] time = timeInfo[1].Split(':');

            int year = Convert.ToInt16(dateString[2]);
            int month = Convert.ToInt16(dateString[0]);
            int date = Convert.ToInt16(dateString[1]);

            int hr = Convert.ToInt16(time[0]);
            int min = Convert.ToInt16(time[1]);
            int sec = Convert.ToInt16(time[2]);

            if (timeInfo[2].Equals("PM") && hr != 12)
            {
                hr += 12;
            }

            if (timeInfo[2].Equals("AM") && hr == 12)
            {
                hr = 0;
            }

            return new DateTime(year, month, date, hr, min, sec, DateTimeKind.Local);
        }

        //======================== Service Status

        /// <summary>
        /// Service state status codes
        /// </summary>
        public enum ServiceState
        {
            SERVICE_STOPPED = 0x00000001,
            SERVICE_START_PENDING = 0x00000002,
            SERVICE_STOP_PENDING = 0x00000003,
            SERVICE_RUNNING = 0x00000004,
            SERVICE_CONTINUE_PENDING = 0x00000005,
            SERVICE_PAUSE_PENDING = 0x00000006,
            SERVICE_PAUSED = 0x00000007,
        }

        /// <summary>
        /// Service Status struct
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct ServiceStatus
        {
            public long dwServiceType;
            public ServiceState dwCurrentState;
            public long dwControlsAccepted;
            public long dwWin32ExitCode;
            public long dwServiceSpecificExitCode;
            public long dwCheckPoint;
            public long dwWaitHint;
        };

        /// <summary>
        /// Sets the service status
        /// </summary>
        /// <param name="handle">Service handle</param>
        /// <param name="serviceStatus">Service status code</param>
        /// <returns></returns>
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus serviceStatus);

        //========================= Needed to use self-signed servers

        /// <summary>
        /// Ensures that our custom certificate validation has been applied
        /// </summary>
        public static void EnsureCertificateValidation()
        {
            if (!INITIALIZED)
            {
                ServicePointManager.ServerCertificateValidationCallback += new System.Net.Security.RemoteCertificateValidationCallback(CustomCertificateValidation);
                INITIALIZED = true;
            }
        }

        /// <summary>
        /// Ensures that server certificate is authenticated
        /// </summary>
        private static bool CustomCertificateValidation(object sender, X509Certificate cert, X509Chain chain, System.Net.Security.SslPolicyErrors error)
        {
            return true;
        }
    }
}
