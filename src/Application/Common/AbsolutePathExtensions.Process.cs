using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Application.Common;

public static partial class AbsolutePathExtensions
{
    /// <summary>
    /// Gets a list of processes that are currently locking the specified file or any file within the specified directory.
    /// </summary>
    /// <param name="path">The path to the file or directory to check for locked files.</param>
    /// <returns>A task representing the asynchronous operation that returns a list of processes locking the file(s).</returns>
    public static async Task<Process[]> GetProcesses(this AbsolutePath path)
    {
        List<Process> processes = [];
        await Task.Run(() =>
        {
            if (path.FileExists())
            {
                processes.AddRange(WhoIsLocking(path));
            }
            else if (path.DirectoryExists())
            {
                var fileMap = GetFileMap(path);

                foreach (var file in fileMap.Files)
                {
                    processes.AddRange(WhoIsLocking(file));
                }
            }
        });
        return [.. processes];
    }

    private static List<Process> WhoIsLocking(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WhoIsLockingWindows(path);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return WhoIsLockingLinux(path);
        }

        throw new NotSupportedException();
    }

    #region Windows Native File Management

    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    const int RmRebootReasonNone = 0;
    const int CCH_RM_MAX_APP_NAME = 255;
    const int CCH_RM_MAX_SVC_NAME = 63;

    enum RM_APP_TYPE
    {
        RmUnknownApp = 0,
        RmMainWindow = 1,
        RmOtherWindow = 2,
        RmService = 3,
        RmExplorer = 4,
        RmConsole = 5,
        RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
        public string strServiceShortName;

        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint pSessionHandle,
                                          UInt32 nFiles,
                                          string[] rgsFilenames,
                                          UInt32 nApplications,
                                          [In] RM_UNIQUE_PROCESS[] rgApplications,
                                          UInt32 nServices,
                                          string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint dwSessionHandle,
                                out uint pnProcInfoNeeded,
                                ref uint pnProcInfo,
                                [In, Out] RM_PROCESS_INFO[] rgAffectedApps,
                                ref uint lpdwRebootReasons);
#pragma warning restore SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute' to generate P/Invoke marshalling code at compile time

    private static List<Process> WhoIsLockingWindows(string path)
    {
        string key = Guid.NewGuid().ToString();
        List<Process> processes = [];

        int res = RmStartSession(out uint handle, 0, key);

        if (res != 0)
            throw new Exception("Could not begin restart session.  Unable to determine file locker.");

        try
        {
            const int ERROR_MORE_DATA = 234;
            uint pnProcInfo = 0,
                 lpdwRebootReasons = RmRebootReasonNone;

            string[] resources = [path]; // Just checking on one resource.

            res = RmRegisterResources(handle, (uint)resources.Length, resources, 0, [], 0, []);

            if (res != 0)
                throw new Exception("Could not register resource.");

            //Note: there's a race condition here -- the first call to RmGetList() returns
            //      the total number of process. However, when we call RmGetList() again to get
            //      the actual processes this number may have increased.
            res = RmGetList(handle, out uint pnProcInfoNeeded, ref pnProcInfo, [], ref lpdwRebootReasons);

            if (res == ERROR_MORE_DATA)
            {
                // Create an array to store the process results
                RM_PROCESS_INFO[] processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                pnProcInfo = pnProcInfoNeeded;

                // Get the list
                res = RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);

                if (res == 0)
                {
                    processes = new List<Process>((int)pnProcInfo);

                    // Enumerate all of the results and add them to the 
                    // list to be returned
                    for (int i = 0; i < pnProcInfo; i++)
                    {
                        try
                        {
                            processes.Add(Process.GetProcessById(processInfo[i].Process.dwProcessId));
                        }
                        // catch the error -- in case the process is no longer running
                        catch (ArgumentException) { }
                    }
                }
                else
                    throw new Exception("Could not list processes locking resource.");
            }
            else if (res != 0)
                throw new Exception("Could not list processes locking resource. Failed to get size of result.");
        }
        finally
        {
            _ = RmEndSession(handle);
        }

        return processes;
    }

    #endregion

    #region Linux Native File Management

    private static List<Process> WhoIsLockingLinux(string path)
    {
        throw new NotImplementedException();
    }

    #endregion
}
