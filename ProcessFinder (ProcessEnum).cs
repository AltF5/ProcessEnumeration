﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

// "Ctrl M, O is your Friend"
//
// Author:      https://github.com/AltF5
// Created:     November 2020
//
// What:
// What is this ProcessFinder.cs file?
//      This is a fast, light-weight, self-contained, grab-n-go, 0-dependency Utility class housing 3x static classes & static methods.
//      I try to opt for minimalism and a "try your best" approach to all implemented methods, avoiding blowups (albeit perhaps somewhere, in some cases, could swallow possible Exceptions, that I'm sure could be improved. Please by all means.)
//      I typically opt for Win32 (Windows API) or ntdll (Native API) [If I don't pull out my hair over PInvoking it] over .NET implementations, unless the .NET src reveals otherwise. In this case the .NET process enum appears flawed from my tests (perhaps due to calling from a lower integrity process (Medium IL) where not all processes are returned)
//
//      Overall, this is a culmination of needs, research, and Native API tests. 
//      Starting point references were best-attempted to be placed where suited, such as which methods are based on what StackOverflow answers, github declarations, and msdn forums.
//      It was also helpful to utilize the ["released"] Windows XP SP1 src as reference to some questions on how things are implemented within (such as toolhelp.c)
//          which reveals how Process and Thread enum via toolhelp utilize the same NTQSI call, but Module enum is a different Rtl Debugger call
//
//      It was created Nov 2020, but some methods I snagged from other code projects I did (hence there many be some dates laying around like "Created 2017" etc
//
//      3x of the classes this file contains:
//
//              1 - A Process Terminator
//                      > PID  - Posts WM_QUIT and WM_CLOSE for Grace       |       OpenProcess(PROCESS_TERMINATE) + TerminateProcess call for Force
//                      > Name - Enumerates all processes, and terminates all matching (Gracefully or Forcefully)
//                      > Window Title  - GetWindowThreadProcessId for hWindow --> PID
//
//              2 - A robust process enumerator via Toolhelp or Native (NtQuerySystemInformation)
//                      > Obtains desried information, including a full module list, Integrity, Privileges, etx
//
//              3 - A process information query class to read info such as the Process Command Line, or Parent PID from the PEB
//
//              4 - ******* TO DO ******** - File unlocker via 'Rm' (Resource Manager) APIs
//
// Why:
// Why the need for the ProcessEnum class?
//      - I've noticed the .NET GetAllProcesses doesn't always (almost always!) load all processes, and GetProcessesByName does not always find the process requested.
//          I haven't investigated too deep into the "why" since I already knew the "how" for implementing a true Process Enumeration via Windows API
//          From initial tests it has to do if the caller (this code) of System.Diagnostics.Process.GetProcesses() or .GetProcessesByName("notepad.exe") isn't running elevated (this is of Medium IL)
//
//      - I have noticed very slow performance with the .NET Process class, and thus want to avoid using it
//          It was especially apparent when utilizing Process.GetProcessById for every process on the system, which was/is VERY slow -- https://github.com/dotnet/runtime/issues/20725
//          This process class is significantly more efficient
//
//      - It was also good to experiment how processes respond to all of their threads being posted the WM_QUIT and WM_CLOSE window messages for graceful & proper
//          process termination, rather than just blindly calling TerminateProcess without giving it a chance to perform its cleanup needed.
//
//
//
//
// TO DO: 
//      - 11-11-2020 - To integrate: 4th features: ProcessFileUtil for Rm file unlocking
//
//
//
// Notable methods:
//      - ProcessTerminateGraceful.TerminateProcessGraceful("notepad.exe");
//      - ProcessTerminateGraceful.TerminateProcessForcefulByWindowName("%notepad++%");
//      - ProcessTerminateGraceful.GetProcessByFullFileNameWin32(@"c:\windows\explorer.exe");
//      
//      - ProcessTerminateGraceful.CloseAllExplorerWindows();
//      - ProcessTerminateClean.RestartExplorerGracefully();
//      - ProcessTerminateClean.TerminateExplorerGracefully();
//      
//      - var test = ProcessTerminateGraceful.CloseAllWindows(true, true)
//
//      - var ps = ProcessEnum.EnumProcesses().OrderByDescending(x => x.StartOrderFromEnum).ToList();


#region 1 - Process Termination via Gracefully & Forcefully

public static class ProcessTerminateGraceful
{
    #region -- APIs - Windows, Threads --

    #region Thread Windows

    delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    #endregion

    #region Windows Enum (via GetWindow) & Message sending

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);



    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    const int WM_CLOSE = 0x0010;
    const uint WM_QUIT = 0x0012;

    [DllImport("user32")]
    static extern IntPtr GetWindow(IntPtr hwnd, int wCmd);

    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetShellWindow();

    [DllImport("user32", EntryPoint = "GetWindowLongA")]
    static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32")]
    static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32", EntryPoint = "GetWindowTextA")]
    static extern int GetWindowText(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

    const int GWL_ID = (-12);
    const int GW_HWNDNEXT = 2;
    const int GW_CHILD = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    #endregion

    #endregion

    #region -- User-defined Data Structures (Window Data Structure) --

    public class Window
    {
        public string Title;
        public string Class;
        public IntPtr Handle;
    }

    #endregion

    #region Last Err

    static int _lastAPIError = 0;

    public static int LastAPIErrorCode
    {
        get
        {
            return _lastAPIError;
        }
    }

    #endregion

    #region Close All Windows (Send WM_QUIT + WM_Close to all Thread windows)

    /// <summary>
    /// NOTICE: Best to run Elevated (High IL), otherwise PostMessage will fail to elevated apps (if this application is not running elevated itself)
    /// "Requests" all Process' Thread's Windows close in the entire OS by sending WM_QUIT & WM_CLOSE
    /// 
    /// Note: For PostMessage to succeed, then current application must be running at or Equal to the same IL (Integrity Level) as what is being sent
    ///       Otherwise Access Denied will occur when sending that window message to a higher IL process. UIPI is what blocks it (sidenote: which could be disabled via .dll injection)
    /// </summary>
    public static void CloseAllWindows(bool skipBrowsers = false, bool skipVisualStudio = false, List<string> processNamesToExclude = null)
    {
        // Example call:
        //
        //      List<string> exclude = new List<string>();
        //      exclude.Add("aida64.exe");
        //      exclude.Add("devenv.exe");
        //      exclude.Add("DisplayFusion.exe");
        //      exclude.Add("googledrivesync.exe");
        //      exclude.Add("icue.exe");
        //      exclude.Add("notepad++.exe");
        //      exclude.Add("NZXT CAM.exe");
        //      exclude.Add("Plex Media Server.exe");
        //      exclude.Add("ProcessHacker.exe");
        //      exclude.Add("setpoint.exe");
        //      exclude.Add("snagit32.exe");
        //      exclude.Add("treesize.exe");
        //      exclude.Add("teamviewer.exe");
        //      exclude.Add("XYplorer.exe");
        //      ProcessTerminateGraceful.CloseAllWindows(true, true, exclude);

        // Go ahead and handle explorer 1st (could do it last too, which is fine)
        CloseAllExplorerWindows();

        List<ProcessEnum.ProcessInfo> all = ProcessEnum.EnumProcessesAndThreads_Native_MoreInfoAndFast();

        // This is still causing the taskbar and windows explorer to terminiate surprisingly
        // Must ALSO NOT send this to PID 0 (Idle process) which will send the message to Windows Explorer to quit as well (interesting)
        List<ProcessEnum.ProcessInfo> processesMinusExplorer = all.Where(x => x.Name.ToLower() != "explorer.exe" && x.PID > 0).ToList();

        if (skipBrowsers)
        {
            processesMinusExplorer = processesMinusExplorer.Where(x =>
            x.Name.ToLower() != "chrome.exe" &&
            x.Name.ToLower() != "firefox.exe" &&
            x.Name.ToLower() != "msedge.exe").ToList();
        }

        if (skipVisualStudio)
        {
            processesMinusExplorer = processesMinusExplorer.Where(x =>
                x.Name.ToLower() != "devenv.exe").ToList();
        }

        if(processNamesToExclude != null)
        {
            // From: stackoverflow.com/a/22160480/5555423 - Exclude items from a list
            processesMinusExplorer = processesMinusExplorer
                .Except(processNamesToExclude, mainList => mainList.Name.ToLower(), exlList => exlList.ToLower()).ToList();
        }

        foreach (ProcessEnum.ProcessInfo p in processesMinusExplorer)
        {
            TerminateProcessGraceful(p);
        }
    }

    public static void CloseAllExplorerWindows()
    {
        // Explorer somewhat after this, when opening new windows

        IntPtr hWindow_TopDesktopWindow = GetDesktopWindow();
        IntPtr hWindow_TopShellWindow = GetShellWindow();               // Progman : Program Manager

        IntPtr hWindow_Taskbar = FindWindow("Shell_TrayWnd", "");
        IntPtr hWindow_Taskbar2 = FindWindow("Shell_SecondaryTrayWnd", "");                             // Creates 1 per monitor
        uint threadIDOfTaskbarToSkip = GetWindowThreadProcessId(hWindow_Taskbar, out uint ignore);
        uint threadIDOfTaskbarToSkip2 = GetWindowThreadProcessId(hWindow_Taskbar2, out uint ignore2);

        foreach (ProcessEnum.ProcessInfo p in ProcessEnum.FindProcessByName_NativeAPI("explorer.exe"))
        {
            foreach (int TID in p.ThreadIDs)
            {
                if (TID != threadIDOfTaskbarToSkip && TID != threadIDOfTaskbarToSkip2)
                {
                    EnumThreadWindows((uint)TID, delegate (IntPtr hWnd, IntPtr lParam)
                    {
                        if (hWnd != hWindow_TopDesktopWindow && hWnd != hWindow_TopShellWindow)
                        {

                            // Instead, better to whitelist, and ONLY close CabinetWClass windows which are explorer windows
                            // This way we are not closing window that cause issues it appears (despite whitelisting the Taskbar threads, Desktop and Shell window, etc)
                            StringBuilder sbClassText = new StringBuilder(256);
                            int ret = GetClassName(hWnd, sbClassText, sbClassText.Capacity);
                            string className = sbClassText.ToString();

                            if (className.ToLower() == "CabinetWClass".ToLower())
                            {
                                // Will also send WM_QUIT first, because why not -- https://stackoverflow.com/a/3155879
                                //      WM_CLOSE vs WM_QUIT vs WM_DESTROY
                                //      WM_CLOSE = [X] button
                                //      WM_QUIT = WM_QUIT message is not related to any window (the hwnd got from GetMessage is NULL and no window procedure is called). This message indicates that the message loop should be stopped and application should be closed. When GetMessage reads WM_QUIT it returns 0 to indicate that. 
                                PostMessage(hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);


                                // Request that all windows for this thread be closed
                                PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                            }


                            //PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                        }
                        return true;

                    }, IntPtr.Zero);
                }
            }
        }

        System.Threading.Thread.Sleep(50);

        Window[] windowsFound = FindWindowLike("Shut Down Windows");
        foreach (var w in windowsFound)
        {
            if (w.Handle != IntPtr.Zero)
            {
                // Request that the window be closed
                bool did = PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    #endregion

    #region Terminate Process gracefully (window closing via Msg send)

    /// <summary>
    /// Send WM_QUIT + WM_Close to all windows for all thread within a process
    /// IMPORTANT: This will ONLY work if the current process is running at the same IL (Integrity Level) or higher. Otherwise PostMessage will be deflected with error 5 (Access denied)
    /// </summary>
    public static void TerminateProcessGraceful(int PID)
    {
        TerminateProcessGraceful(ProcessEnum.LoadProcessInfoByPID(PID, ProcessEnum.EnumInfoRequest.Basic));     // Just need PIDs and TIDs
    }

    /// <summary>
    /// Send WM_QUIT + WM_Close to all windows for all thread within a process
    /// IMPORTANT: This will ONLY work if the current process is running at the same IL (Integrity Level) or higher. Otherwise PostMessage will be deflected with error 5 (Access denied)
    /// </summary>
    public static void TerminateProcessGraceful(ProcessEnum.ProcessInfo process)
    {
        // Based on:
        // social.msdn.microsoft.com/Forums/vstudio/en-US/82992842-80eb-43c8-a9e6-0a6a1d19b00f/terminating-a-process-in-a-friendly-way?forum=csharpgeneral

        if (process.WasProcessRunning)
        {
            // Try closing application by sending WM_CLOSE to all child windows in all threads.
            foreach (int TID in process.ThreadIDs)
            {
                EnumThreadWindows((uint)TID, delegate (IntPtr hWnd, IntPtr lParam)
                {

                    // Will also send WM_QUIT first, because why not -- https://stackoverflow.com/a/3155879
                    //      WM_CLOSE vs WM_QUIT vs WM_DESTROY
                    //      WM_CLOSE = [X] button
                    //      WM_QUIT = WM_QUIT message is not related to any window (the hwnd got from GetMessage is NULL and no window procedure is called). This message indicates that the message loop should be stopped and application should be closed. When GetMessage reads WM_QUIT it returns 0 to indicate that. 
                    bool did = PostMessage(hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);


                    // Request that all windows for this thread be closed
                    did = PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    if (!did)
                    {
                        _lastAPIError = Marshal.GetLastWin32Error();
                    }

                    return true;

                }, IntPtr.Zero);
            }
        }

    }

    /// <summary>
    /// Send WM_QUIT + WM_Close to all windows for all thread within all matching processes located by name (ProcessEnum).
    /// IMPORTANT: This will ONLY work if the current process is running at the same IL (Integrity Level) or higher. Otherwise PostMessage will be deflected with error 5 (Access denied)
    /// </summary>
    public static void TerminateProcessGraceful(string processName)
    {
        TerminateProcessGraceful(new string[] { processName });
    }

    /// <summary>
    /// Send WM_QUIT + WM_Close to all windows for all thread within all matching processes located by name (ProcessEnum).
    /// IMPORTANT: This will ONLY work if the current process is running at the same IL (Integrity Level) or higher. Otherwise PostMessage will be deflected with error 5 (Access denied)
    /// </summary>
    public static void TerminateProcessGraceful(string[] processNames)
    {
        var foundProcesses = new List<ProcessEnum.ProcessInfo[]>();

        foreach (string processName in processNames)
        {
            ProcessEnum.ProcessInfo[] p = ProcessEnum.FindProcessByName(processName).ToArray();
            foundProcesses.Add(p);
        }

        foreach (ProcessEnum.ProcessInfo[] matchingProcsByName in foundProcesses)
        {
            foreach (ProcessEnum.ProcessInfo proc in matchingProcsByName)
            {

                // First try this...
                // if (proc.MainWindowHandle != IntPtr.Zero)
                // {
                //     // Try to close main window.
                //     bool didClose = proc.CloseMainWindow();
                // }

                // Try closing application by sending WM_CLOSE to all child windows in all threads.
                foreach (int TID in proc.ThreadIDs)
                {
                    EnumThreadWindows((uint)TID, delegate (IntPtr hWnd, IntPtr lParam)
                    {
                        // Will also send WM_QUIT first, because why not -- https://stackoverflow.com/a/3155879
                        //      WM_CLOSE vs WM_QUIT vs WM_DESTROY
                        //      WM_CLOSE = [X] button
                        //      WM_QUIT = WM_QUIT message is not related to any window (the hwnd got from GetMessage is NULL and no window procedure is called). This message indicates that the message loop should be stopped and application should be closed. When GetMessage reads WM_QUIT it returns 0 to indicate that. 
                        bool did = PostMessage(hWnd, WM_QUIT, IntPtr.Zero, IntPtr.Zero);


                        // Request that all windows for this thread be closed
                        did = PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        if(!did)
                        {
                            _lastAPIError = Marshal.GetLastWin32Error();
                        }
                        return did;

                    }, IntPtr.Zero);
                }


            }
        }
    }

    /// <summary>
    /// Does NOT require a ProcessEnum.
    /// </summary>
    public static void TerminateProcessGracefulByWindowName(string windowNameLike)
    {
        // Example call:
        //      ProcessTerminateGraceful.TerminateProcessGracefulByWindowName("%notepad++%");

        Window[] windows = FindWindowLike(windowNameLike, null);

        foreach (var window in windows)
        {
            // Window handle --> Thread ID and Process ID
            uint tid_NotUsed = GetWindowThreadProcessId(window.Handle, out uint pid);
            TerminateProcessGraceful((int)pid);
        }
    }


    #endregion

    #region Terminate process forcefully (requires being able to open the process with PROCESS_TERMINATE)

    [DllImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess_API(IntPtr hProcess, uint uExitCode);

    /// <summary>
    /// Terminates 1 process by PID, by Force (TerminateProcess call).
    /// Does NOT require a ProcessEnum.
    /// </summary>
    public static bool TerminateProcessForceful(int PID)
    {
        IntPtr hProcess = ProcessEnum.OpenProcess(ProcessEnum.ProcessAccessFlags.Terminate, false, PID);
        if(hProcess == IntPtr.Zero)
        {
            return false;
        }
        else
        {
            bool did = TerminateProcess_API(hProcess, 0);
            if(!did)
            {
                _lastAPIError = Marshal.GetLastWin32Error();
                return false;
            }
            else
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Terminates all matching process names by Force (TerminateProcess call)
    /// </summary>
    public static List<ProcessEnum.ProcessInfo> TerminateProcessForceful(string processName)
    {
        List<ProcessEnum.ProcessInfo> processesFoundRet = ProcessEnum.GetProcessesByName(processName);

        foreach (var p in processesFoundRet)
        {
            IntPtr hProcess = ProcessEnum.OpenProcess(ProcessEnum.ProcessAccessFlags.Terminate, false, p.PID);
            if (hProcess == IntPtr.Zero)
            {
                //return false;
            }
            else
            {
                bool did = TerminateProcess_API(hProcess, 0);
                if (!did)
                {
                    _lastAPIError = Marshal.GetLastWin32Error();
                    //return false;
                }
                else
                {
                    //return true;
                }
            }
        }

        return processesFoundRet;
    }

    /// <summary>
    /// Does NOT require a ProcessEnum.
    /// </summary>
    public static void TerminateProcessForcefulByWindowName(string windowNameLike)
    {
        // Example call:
        //      ProcessTerminateGraceful.TerminateProcessForcefulByWindowName("%notepad++%");

        Window[] windows = FindWindowLike(windowNameLike, null);

        foreach (var window in windows)
        {
            // Window handle --> Thread ID and Process ID
            uint tid_NotUsed = GetWindowThreadProcessId(window.Handle, out uint pid);
            TerminateProcessForceful((int)pid);
        }
    }


    // ProcessEnum.FindProcessByName(processName).ToArray();

    #endregion

    #region Explorer specific window closing & termination

    public static void TerminateExplorerGracefully()
    {
        // From: stackoverflow.com/a/5705965
        IntPtr hWndTray = FindWindow("Shell_TrayWnd", "");
        bool did = PostMessage(hWndTray, 0x5B4, IntPtr.Zero, IntPtr.Zero);     // Special undocumented WM_USER window message
        if (!did)
        {
            _lastAPIError = Marshal.GetLastWin32Error();
        }
    }

    /// <summary>
    /// MUST be running as x64 (or disable Wow64) otherwise the explorer.exe relaunch will be an explorer Window, instead of the shell
    /// </summary>
    public static void RestartExplorerGracefully()
    {
        TerminateExplorerGracefully();

        // Required. Bare minimum is 1.5seconds it appears
        System.Threading.Thread.Sleep(1500);

        // Ideally restart with Medium IL, if the registry modification is in place, but this will suffice for now
        System.Diagnostics.Process.Start("explorer.exe");
    }


    #endregion


    #region FindWindowLike (for Terminate process be Window Name)

    /// <summary>
    /// Uses FindWindowRecursive
    /// Current Wildcard Support: % at the Start or End
    /// 
    /// Search the entire window list (all windows and child windows) starting either from the main Desktop parent window (if hwndStart = IntPtr.Zero)
    /// or starting from the parent window supplied. Uses recursing to enumerate the child windows.
    /// Supports wild cards at the start and end of findText and findClassName. Not currently in the middle.
    /// This includes finding button names where FindWindowEx fails
    /// </summary>
    /// <param name="hwndStart">Where to start the search in the window tree</param>
    /// <param name="findText">The window title / text</param>
    /// <param name="findClassName">The window class</param>
    /// <returns>List of matching windows</returns>
    public static Window[] FindWindowLike(string findText, string findClassName = null, IntPtr hwndStart = default(IntPtr))
    {
        // From - experts-exchange.com/questions/21611201/I-need-FindWindowLike-for-C.html

        if (hwndStart == IntPtr.Zero)
        {
            hwndStart = GetDesktopWindow();     // The topmost window to start the search
        }

        List<Window> windows = FindWindowRecursive(hwndStart, findText, findClassName, true);
        return windows.ToArray();
    }

    private static List<Window> FindWindowRecursive(IntPtr hwndStart, string findText, string findClassName,
        bool firstCall = true,
        bool wasWildCardSupplied_Previous = false,
        bool useStartsWithForText_Previous = false,
        bool useStartsWithForClass_Previous = false)
    {
        //
        // Modified from - experts-exchange.com/questions/21611201/I-need-FindWindowLike-for-C.html
        //

        // Right now only supports % at the start and end of the string, not in the middle

        var list = new List<Window>();
        if (hwndStart == IntPtr.Zero)
        {
            hwndStart = GetDesktopWindow();
        }

        IntPtr hWindowChild = GetWindow(hwndStart, GW_CHILD);

        bool textSpecified = !string.IsNullOrWhiteSpace(findText);
        bool classSpecified = !string.IsNullOrWhiteSpace(findClassName);

        const string WILDCARD = "%";
        bool wildcardSuppliedNowOrBefore = false;

        // Only analyzed when wildcardSuppliedNowOrBefore is true
        bool useStartsWithForTextNowOrBefore = true;
        bool useStartsWithForClassNowOrBefore = true;

        if (firstCall)
        {
            wildcardSuppliedNowOrBefore = findText.Contains(WILDCARD);

            if (wildcardSuppliedNowOrBefore)
            {
                if (textSpecified && findText.Contains(WILDCARD))
                {
                    if (findText.StartsWith(WILDCARD))
                    {
                        useStartsWithForTextNowOrBefore = false;       // use EndsWith instead
                    }

                    // Remove wildcard
                    findText = findText.Replace(WILDCARD, "");
                }

                if (classSpecified && findClassName.Contains(WILDCARD))
                {
                    if (findClassName.StartsWith(WILDCARD))
                    {
                        useStartsWithForClassNowOrBefore = false;      // use EndsWith instead
                    }

                    // Remove wildcard
                    findClassName = findClassName.Replace(WILDCARD, "");
                }
            }

            // Set the values
            wasWildCardSupplied_Previous = wildcardSuppliedNowOrBefore;
            useStartsWithForText_Previous = useStartsWithForTextNowOrBefore;
            useStartsWithForClass_Previous = useStartsWithForClassNowOrBefore;
        }
        else
        {
            // Reload the prior call's values
            wildcardSuppliedNowOrBefore = wasWildCardSupplied_Previous;
            useStartsWithForTextNowOrBefore = useStartsWithForText_Previous;
            useStartsWithForClassNowOrBefore = useStartsWithForClass_Previous;
        }


        while (hWindowChild != IntPtr.Zero)
        {
            //
            // Recursion - If there is a child window search deeper until locating a child of that child that returns IntPtr.Zero for GetWindow(GW_CHILD)
            //

            // Recursively search for child windows.
            list.AddRange(FindWindowRecursive(hWindowChild, findText, findClassName, false, wasWildCardSupplied_Previous, useStartsWithForText_Previous, useStartsWithForClass_Previous));

            StringBuilder text = new StringBuilder(255);
            int rtn = GetWindowText(hWindowChild, text, 255);
            string windowText = text.ToString();
            windowText = windowText.Substring(0, rtn);

            StringBuilder cls = new StringBuilder(255);
            rtn = GetClassName(hWindowChild, cls, 255);
            string className = cls.ToString();
            className = className.Substring(0, rtn);

            // --- DBG ---
            //
            //if (GetParent(hwnd) != IntPtr.Zero)
            //{
            //    rtn = GetWindowLong(hwnd, GWL_ID);
            //}

            // --- DBG ---
            //
            //if(windowText.Contains("Create"))
            //{
            //    int break0 = 7;
            //}

            if (!textSpecified && !classSpecified)
            {
                // Case 1 - Neither supplied - Add all windows
                Window currentWindow = new Window();
                currentWindow.Title = windowText;
                currentWindow.Class = className;
                currentWindow.Handle = hWindowChild;
                list.Add(currentWindow);
            }
            else if (textSpecified && !classSpecified)
            {
                // Case 2 - Lookup by Text only
                if (windowText.Length > 0)
                {
                    if (wildcardSuppliedNowOrBefore)
                    {
                        if (useStartsWithForTextNowOrBefore && windowText.StartsWith(findText, StringComparison.CurrentCultureIgnoreCase)
                            || (!useStartsWithForTextNowOrBefore && windowText.EndsWith(findText, StringComparison.CurrentCultureIgnoreCase)))
                        {
                            Window currentWindow = new Window();
                            currentWindow.Title = windowText;
                            currentWindow.Class = className;
                            currentWindow.Handle = hWindowChild;
                            list.Add(currentWindow);
                        }
                    }
                    else
                    {
                        if (windowText.ToLower() == findText.ToLower())
                        {
                            Window currentWindow = new Window();
                            currentWindow.Title = windowText;
                            currentWindow.Class = className;
                            currentWindow.Handle = hWindowChild;
                            list.Add(currentWindow);
                        }
                    }
                }
            }
            else if (!textSpecified && classSpecified)
            {
                // Case 3 - Lookup by Class only
                if (className.Length > 0)
                {
                    if (wildcardSuppliedNowOrBefore)
                    {
                        if (useStartsWithForClassNowOrBefore && className.StartsWith(findClassName, StringComparison.CurrentCultureIgnoreCase)
                            || (!useStartsWithForClassNowOrBefore && className.EndsWith(findClassName, StringComparison.CurrentCultureIgnoreCase)))
                        {
                            Window currentWindow = new Window();
                            currentWindow.Title = windowText;
                            currentWindow.Class = className;
                            currentWindow.Handle = hWindowChild;
                            list.Add(currentWindow);
                        }
                    }
                    else
                    {
                        if (className.ToLower() == findClassName.ToLower())
                        {
                            Window currentWindow = new Window();
                            currentWindow.Title = windowText;
                            currentWindow.Class = className;
                            currentWindow.Handle = hWindowChild;
                            list.Add(currentWindow);
                        }
                    }
                }
            }
            else if (textSpecified && classSpecified)
            {
                // Case 4 - Both Text and Class supplied
                if (wildcardSuppliedNowOrBefore)
                {
                    if (windowText.Length > 0
                        &&
                        (useStartsWithForTextNowOrBefore && windowText.StartsWith(findText, StringComparison.CurrentCultureIgnoreCase)
                        || (!useStartsWithForTextNowOrBefore && windowText.EndsWith(findText, StringComparison.CurrentCultureIgnoreCase)))

                        &&
                        (useStartsWithForClassNowOrBefore && className.StartsWith(findClassName, StringComparison.CurrentCultureIgnoreCase)
                        || (!useStartsWithForClassNowOrBefore && className.EndsWith(findClassName, StringComparison.CurrentCultureIgnoreCase))))

                    {
                        Window currentWindow = new Window();
                        currentWindow.Title = windowText;
                        currentWindow.Class = className;
                        currentWindow.Handle = hWindowChild;
                        list.Add(currentWindow);
                    }
                }
                else
                {
                    if (windowText.ToLower() == findText.ToLower() &&
                        className.ToLower() == findClassName.ToLower())
                    {
                        Window currentWindow = new Window();
                        currentWindow.Title = windowText;
                        currentWindow.Class = className;
                        currentWindow.Handle = hWindowChild;
                        list.Add(currentWindow);
                    }
                }
            }

            // Get the next sibling to this child
            hWindowChild = GetWindow(hWindowChild, GW_HWNDNEXT);
        }

        return list;

    }

    #endregion
}

#endregion

#region 2 - [A robust] ProcessEnum

public static class ProcessEnum
{
    #region -- APIs --

    #region Information Classes

    /// <summary>
    /// For CreateToolhelp32Snapshot
    /// </summary>
    [Flags]
    public enum SnapshotFlags : uint
    {
        HeapList = 1,           // TH32CS_SNAPHEAPLIST
        Process = 2,            // TH32CS_SNAPPROCESS           << Process and Thread implementation in toolhelp.c in Windows XP SP1 source code shows that Processes & Threads are done with the same NtQuerySystemInformation InfoClass call
        Thread = 4,             // TH32CS_SNAPTHREAD            << ""
        Module = 8,             // TH32CS_SNAPMODULE
        Module32 = 16,          // TH32CS_SNAPMODULE32 - Include modules from WOW64 processes
        Inherit = 0x80000000,   // 2147483648
        All = 0x0000001F        // 31
    }

    #endregion

    #region Native undocumented Information classes & structs (ntdll)

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;

        public int Size
        {
            get { return Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RTL_USER_PROCESS_PARAMETERS
    {
        byte b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10, b11, b12, b13, b14, b15;          // BYTE Reserved1[16];
        IntPtr ip0, ip1, ip2, ip3, ip4, ip5, ip6, ip7, ip8, ip9;                            // PVOID Reserved2[10];
        public UNICODE_STRING ImagePathName;
        public UNICODE_STRING CommandLine;
    };

    [StructLayout(LayoutKind.Sequential)]
    struct PEB
    {
        IntPtr Reserved1;
        IntPtr Reserved2;
        IntPtr Reserved3;
        public IntPtr Ldr;                  // struct PEB_LDR_DATA*
        public IntPtr ProcessParameters;    // RTL_USER_PROCESS_PARAMETERS*
        // ...
    }

    enum PROCESSINFOCLASS : int
    {
        ProcessBasicInformation = 0, // 0, q: PROCESS_BASIC_INFORMATION, PROCESS_EXTENDED_BASIC_INFORMATION
        ProcessQuotaLimits, // qs: QUOTA_LIMITS, QUOTA_LIMITS_EX
        ProcessIoCounters, // q: IO_COUNTERS
        ProcessVmCounters, // q: VM_COUNTERS, VM_COUNTERS_EX
        ProcessTimes, // q: KERNEL_USER_TIMES
        ProcessBasePriority, // s: KPRIORITY
        ProcessRaisePriority, // s: ULONG
        ProcessDebugPort, // q: HANDLE
        ProcessExceptionPort, // s: HANDLE
        ProcessAccessToken, // s: PROCESS_ACCESS_TOKEN
        ProcessLdtInformation, // 10
        ProcessLdtSize,
        ProcessDefaultHardErrorMode, // qs: ULONG
        ProcessIoPortHandlers, // (kernel-mode only)
        ProcessPooledUsageAndLimits, // q: POOLED_USAGE_AND_LIMITS
        ProcessWorkingSetWatch, // q: PROCESS_WS_WATCH_INFORMATION[]; s: void
        ProcessUserModeIOPL,
        ProcessEnableAlignmentFaultFixup, // s: BOOLEAN
        ProcessPriorityClass, // qs: PROCESS_PRIORITY_CLASS
        ProcessWx86Information,
        ProcessHandleCount, // 20, q: ULONG, PROCESS_HANDLE_INFORMATION
        ProcessAffinityMask, // s: KAFFINITY
        ProcessPriorityBoost, // qs: ULONG
        ProcessDeviceMap, // qs: PROCESS_DEVICEMAP_INFORMATION, PROCESS_DEVICEMAP_INFORMATION_EX
        ProcessSessionInformation, // q: PROCESS_SESSION_INFORMATION
        ProcessForegroundInformation, // s: PROCESS_FOREGROUND_BACKGROUND
        ProcessWow64Information, // q: ULONG_PTR
        ProcessImageFileName, // q: UNICODE_STRING
        ProcessLUIDDeviceMapsEnabled, // q: ULONG
        ProcessBreakOnTermination, // qs: ULONG
        ProcessDebugObjectHandle, // 30, q: HANDLE
        ProcessDebugFlags, // qs: ULONG
        ProcessHandleTracing, // q: PROCESS_HANDLE_TRACING_QUERY; s: size 0 disables, otherwise enables
        ProcessIoPriority, // qs: ULONG
        ProcessExecuteFlags, // qs: ULONG
        ProcessResourceManagement,
        ProcessCookie, // q: ULONG
        ProcessImageInformation, // q: SECTION_IMAGE_INFORMATION
        ProcessCycleTime, // q: PROCESS_CYCLE_TIME_INFORMATION
        ProcessPagePriority, // q: ULONG
        ProcessInstrumentationCallback, // 40
        ProcessThreadStackAllocation, // s: PROCESS_STACK_ALLOCATION_INFORMATION, PROCESS_STACK_ALLOCATION_INFORMATION_EX
        ProcessWorkingSetWatchEx, // q: PROCESS_WS_WATCH_INFORMATION_EX[]
        ProcessImageFileNameWin32, // q: UNICODE_STRING
        ProcessImageFileMapping, // q: HANDLE (input)
        ProcessAffinityUpdateMode, // qs: PROCESS_AFFINITY_UPDATE_MODE
        ProcessMemoryAllocationMode, // qs: PROCESS_MEMORY_ALLOCATION_MODE
        ProcessGroupInformation,            // q: USHORT[]
        ProcessTokenVirtualizationEnabled, // s: ULONG
        ProcessConsoleHostProcess,          // q: ULONG_PTR
        ProcessWindowInformation,           // 50, q: PROCESS_WINDOW_INFORMATION
        ProcessHandleInformation,           // q: PROCESS_HANDLE_SNAPSHOT_INFORMATION // since WIN8
        ProcessMitigationPolicy,            // s: PROCESS_MITIGATION_POLICY_INFORMATION
        ProcessDynamicFunctionTableInformation,
        ProcessHandleCheckingMode,
        ProcessKeepAliveCount,              // q: PROCESS_KEEPALIVE_COUNT_INFORMATION
        ProcessRevokeFileHandles,           // s: PROCESS_REVOKE_FILE_HANDLES_INFORMATION
        MaxProcessInfoClass
    };


    /// <summary>
    /// For:    NtQuerySystemInformation
    /// Use:    Not needed, but added for completeness / reference of available information
    /// From:   pinvoke.net/default.aspx/ntdll/SYSTEM_INFORMATION_CLASS.html
    /// Info:   geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/class.htm
    /// </summary>
    public enum SYSTEM_INFORMATION_CLASS
    {
        SystemBasicInformation = 0x00,
        SystemProcessorInformation = 0x01,
        SystemPerformanceInformation = 0x02,
        SystemTimeOfDayInformation = 0x03,
        SystemPathInformation = 0x04,
        SystemProcessInformation = 0x05,
        SystemCallCountInformation = 0x06,
        SystemDeviceInformation = 0x07,
        SystemProcessorPerformanceInformation = 0x08,
        SystemFlagsInformation = 0x09,
        SystemCallTimeInformation = 0x0A,
        SystemModuleInformation = 0x0B,
        SystemLocksInformation = 0x0C,
        SystemStackTraceInformation = 0x0D,
        SystemPagedPoolInformation = 0x0E,
        SystemNonPagedPoolInformation = 0x0F,
        SystemHandleInformation = 0x10,
        SystemObjectInformation = 0x11,
        SystemPageFileInformation = 0x12,
        SystemVdmInstemulInformation = 0x13,
        SystemVdmBopInformation = 0x14,
        SystemFileCacheInformation = 0x15,
        SystemPoolTagInformation = 0x16,
        SystemInterruptInformation = 0x17,
        SystemDpcBehaviorInformation = 0x18,
        SystemFullMemoryInformation = 0x19,
        SystemLoadGdiDriverInformation = 0x1A,
        SystemUnloadGdiDriverInformation = 0x1B,
        SystemTimeAdjustmentInformation = 0x1C,
        SystemSummaryMemoryInformation = 0x1D,
        SystemMirrorMemoryInformation = 0x1E,
        SystemPerformanceTraceInformation = 0x1F,
        SystemObsolete0 = 0x20,
        SystemExceptionInformation = 0x21,
        SystemCrashDumpStateInformation = 0x22,
        SystemKernelDebuggerInformation = 0x23,
        SystemContextSwitchInformation = 0x24,
        SystemRegistryQuotaInformation = 0x25,
        SystemExtendServiceTableInformation = 0x26,
        SystemPrioritySeperation = 0x27,
        SystemVerifierAddDriverInformation = 0x28,
        SystemVerifierRemoveDriverInformation = 0x29,
        SystemProcessorIdleInformation = 0x2A,
        SystemLegacyDriverInformation = 0x2B,
        SystemCurrentTimeZoneInformation = 0x2C,
        SystemLookasideInformation = 0x2D,
        SystemTimeSlipNotification = 0x2E,
        SystemSessionCreate = 0x2F,
        SystemSessionDetach = 0x30,
        SystemSessionInformation = 0x31,
        SystemRangeStartInformation = 0x32,
        SystemVerifierInformation = 0x33,
        SystemVerifierThunkExtend = 0x34,
        SystemSessionProcessInformation = 0x35,
        SystemLoadGdiDriverInSystemSpace = 0x36,
        SystemNumaProcessorMap = 0x37,
        SystemPrefetcherInformation = 0x38,
        SystemExtendedProcessInformation = 0x39,
        SystemRecommendedSharedDataAlignment = 0x3A,
        SystemComPlusPackage = 0x3B,
        SystemNumaAvailableMemory = 0x3C,
        SystemProcessorPowerInformation = 0x3D,
        SystemEmulationBasicInformation = 0x3E,
        SystemEmulationProcessorInformation = 0x3F,
        SystemExtendedHandleInformation = 0x40,
        SystemLostDelayedWriteInformation = 0x41,
        SystemBigPoolInformation = 0x42,
        SystemSessionPoolTagInformation = 0x43,
        SystemSessionMappedViewInformation = 0x44,
        SystemHotpatchInformation = 0x45,
        SystemObjectSecurityMode = 0x46,
        SystemWatchdogTimerHandler = 0x47,
        SystemWatchdogTimerInformation = 0x48,
        SystemLogicalProcessorInformation = 0x49,
        SystemWow64SharedInformationObsolete = 0x4A,
        SystemRegisterFirmwareTableInformationHandler = 0x4B,
        SystemFirmwareTableInformation = 0x4C,
        SystemModuleInformationEx = 0x4D,
        SystemVerifierTriageInformation = 0x4E,
        SystemSuperfetchInformation = 0x4F,
        SystemMemoryListInformation = 0x50,
        SystemFileCacheInformationEx = 0x51,
        SystemThreadPriorityClientIdInformation = 0x52,
        SystemProcessorIdleCycleTimeInformation = 0x53,
        SystemVerifierCancellationInformation = 0x54,
        SystemProcessorPowerInformationEx = 0x55,
        SystemRefTraceInformation = 0x56,
        SystemSpecialPoolInformation = 0x57,
        SystemProcessIdInformation = 0x58,
        SystemErrorPortInformation = 0x59,
        SystemBootEnvironmentInformation = 0x5A,
        SystemHypervisorInformation = 0x5B,
        SystemVerifierInformationEx = 0x5C,
        SystemTimeZoneInformation = 0x5D,
        SystemImageFileExecutionOptionsInformation = 0x5E,
        SystemCoverageInformation = 0x5F,
        SystemPrefetchPatchInformation = 0x60,
        SystemVerifierFaultsInformation = 0x61,
        SystemSystemPartitionInformation = 0x62,
        SystemSystemDiskInformation = 0x63,
        SystemProcessorPerformanceDistribution = 0x64,
        SystemNumaProximityNodeInformation = 0x65,
        SystemDynamicTimeZoneInformation = 0x66,
        SystemCodeIntegrityInformation = 0x67,
        SystemProcessorMicrocodeUpdateInformation = 0x68,
        SystemProcessorBrandString = 0x69,
        SystemVirtualAddressInformation = 0x6A,
        SystemLogicalProcessorAndGroupInformation = 0x6B,
        SystemProcessorCycleTimeInformation = 0x6C,
        SystemStoreInformation = 0x6D,
        SystemRegistryAppendString = 0x6E,
        SystemAitSamplingValue = 0x6F,
        SystemVhdBootInformation = 0x70,
        SystemCpuQuotaInformation = 0x71,
        SystemNativeBasicInformation = 0x72,
        SystemErrorPortTimeouts = 0x73,
        SystemLowPriorityIoInformation = 0x74,
        SystemBootEntropyInformation = 0x75,
        SystemVerifierCountersInformation = 0x76,
        SystemPagedPoolInformationEx = 0x77,
        SystemSystemPtesInformationEx = 0x78,
        SystemNodeDistanceInformation = 0x79,
        SystemAcpiAuditInformation = 0x7A,
        SystemBasicPerformanceInformation = 0x7B,
        SystemQueryPerformanceCounterInformation = 0x7C,
        SystemSessionBigPoolInformation = 0x7D,
        SystemBootGraphicsInformation = 0x7E,
        SystemScrubPhysicalMemoryInformation = 0x7F,
        SystemBadPageInformation = 0x80,
        SystemProcessorProfileControlArea = 0x81,
        SystemCombinePhysicalMemoryInformation = 0x82,
        SystemEntropyInterruptTimingInformation = 0x83,
        SystemConsoleInformation = 0x84,
        SystemPlatformBinaryInformation = 0x85,
        SystemPolicyInformation = 0x86,
        SystemHypervisorProcessorCountInformation = 0x87,
        SystemDeviceDataInformation = 0x88,
        SystemDeviceDataEnumerationInformation = 0x89,
        SystemMemoryTopologyInformation = 0x8A,
        SystemMemoryChannelInformation = 0x8B,
        SystemBootLogoInformation = 0x8C,
        SystemProcessorPerformanceInformationEx = 0x8D,
        SystemCriticalProcessErrorLogInformation = 0x8E,
        SystemSecureBootPolicyInformation = 0x8F,
        SystemPageFileInformationEx = 0x90,
        SystemSecureBootInformation = 0x91,
        SystemEntropyInterruptTimingRawInformation = 0x92,
        SystemPortableWorkspaceEfiLauncherInformation = 0x93,
        SystemFullProcessInformation = 0x94,
        SystemKernelDebuggerInformationEx = 0x95,
        SystemBootMetadataInformation = 0x96,
        SystemSoftRebootInformation = 0x97,
        SystemElamCertificateInformation = 0x98,
        SystemOfflineDumpConfigInformation = 0x99,
        SystemProcessorFeaturesInformation = 0x9A,
        SystemRegistryReconciliationInformation = 0x9B,
        SystemEdidInformation = 0x9C,
        SystemManufacturingInformation = 0x9D,
        SystemEnergyEstimationConfigInformation = 0x9E,
        SystemHypervisorDetailInformation = 0x9F,
        SystemProcessorCycleStatsInformation = 0xA0,
        SystemVmGenerationCountInformation = 0xA1,
        SystemTrustedPlatformModuleInformation = 0xA2,
        SystemKernelDebuggerFlags = 0xA3,
        SystemCodeIntegrityPolicyInformation = 0xA4,
        SystemIsolatedUserModeInformation = 0xA5,
        SystemHardwareSecurityTestInterfaceResultsInformation = 0xA6,
        SystemSingleModuleInformation = 0xA7,
        SystemAllowedCpuSetsInformation = 0xA8,
        SystemDmaProtectionInformation = 0xA9,
        SystemInterruptCpuSetsInformation = 0xAA,
        SystemSecureBootPolicyFullInformation = 0xAB,
        SystemCodeIntegrityPolicyFullInformation = 0xAC,
        SystemAffinitizedInterruptProcessorInformation = 0xAD,
        SystemRootSiloInformation = 0xAE,
        SystemCpuSetInformation = 0xAF,
        SystemCpuSetTagInformation = 0xB0,
        SystemWin32WerStartCallout = 0xB1,
        SystemSecureKernelProfileInformation = 0xB2,
        SystemCodeIntegrityPlatformManifestInformation = 0xB3,
        SystemInterruptSteeringInformation = 0xB4,
        SystemSuppportedProcessorArchitectures = 0xB5,
        SystemMemoryUsageInformation = 0xB6,
        SystemCodeIntegrityCertificateInformation = 0xB7,
        SystemPhysicalMemoryInformation = 0xB8,
        SystemControlFlowTransition = 0xB9,
        SystemKernelDebuggingAllowed = 0xBA,
        SystemActivityModerationExeState = 0xBB,
        SystemActivityModerationUserSettings = 0xBC,
        SystemCodeIntegrityPoliciesFullInformation = 0xBD,
        SystemCodeIntegrityUnlockInformation = 0xBE,
        SystemIntegrityQuotaInformation = 0xBF,
        SystemFlushInformation = 0xC0,
        SystemProcessorIdleMaskInformation = 0xC1,
        SystemSecureDumpEncryptionInformation = 0xC2,
        SystemWriteConstraintInformation = 0xC3,
        SystemKernelVaShadowInformation = 0xC4,
        SystemHypervisorSharedPageInformation = 0xC5,
        SystemFirmwareBootPerformanceInformation = 0xC6,
        SystemCodeIntegrityVerificationInformation = 0xC7,
        SystemFirmwarePartitionInformation = 0xC8,
        SystemSpeculationControlInformation = 0xC9,
        SystemDmaGuardPolicyInformation = 0xCA,
        SystemEnclaveLaunchControlInformation = 0xCB,
        SystemWorkloadAllowedCpuSetsInformation = 0xCC,
        SystemCodeIntegrityUnlockModeInformation = 0xCD,
        SystemLeapSecondInformation = 0xCE,
        SystemFlags2Information = 0xCF,
        SystemSecurityModelInformation = 0xD0,
        SystemCodeIntegritySyntheticCacheInformation = 0xD1,
        MaxSystemInfoClass = 0xD2
    }

    /// <summary>
    /// For:    NtQuerySystemInformation (w/ SystemProcessInformation)
    /// What:   Represents SYSTEM_PROCESS_INFORMATION
    /// From:    .NET Source code: https://referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,0c3c811ff2002530
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    class SystemProcessInformation
    {
        // native struct defined in ntexapi.h

        /// <summary>
        /// The offset of the next SystemProcessInformation, which will travel past the previous SystemProcessInformation thread list
        /// </summary>
        public uint NextEntryOffset;

        public uint NumberOfThreads;

        public long SpareLi1;
        public long SpareLi2;
        public long SpareLi3;

        /// <summary>
        /// Start time.
        /// This comes from EPROCESS. See \ntos\ex\sysinfo.c --> ExpCopyProcessInfo
        /// </summary>
        public long CreateTime;

        public long UserTime;
        public long KernelTime;

        public UNICODE_STRING ProcessName;
        public int BasePriority;
        public IntPtr UniqueProcessId;                  // int. Defined as PVOID, but this is actually an int (uint)
        public IntPtr InheritedFromUniqueProcessId;     // int. Defined as PVOID, but this is actually an int (uint)
        public uint HandleCount;
        public uint SessionId;
        public IntPtr PageDirectoryBase;
        public IntPtr PeakVirtualSize;  // SIZE_T
        public IntPtr VirtualSize;
        public uint PageFaultCount;

        // See here for more information regarding mmemory counters    (PROCESS_MEMORY_COUNTERS_EX)  docs.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex
        public IntPtr PeakWorkingSetSize;
        public IntPtr WorkingSetSize;
        public IntPtr QuotaPeakPagedPoolUsage;
        public IntPtr QuotaPagedPoolUsage;
        public IntPtr QuotaPeakNonPagedPoolUsage;
        public IntPtr QuotaNonPagedPoolUsage;

        /// <summary>
        /// AKA 'PrivateUsage' -  The Commit Charge value in bytes for this process. 
        /// Commit Charge is the total amount of "private memory" that the memory manager has committed for a running process.
        /// </summary>
        public IntPtr PagefileUsage;

        /// <summary>
        /// The peak value in bytes of the Commit Charge during the lifetime of this process.
        /// </summary>
        public IntPtr PeakPagefileUsage;

        /// <summary>
        /// the number of private memory pages allocated
        /// </summary>
        public IntPtr PrivatePageCount;

        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;

        // 0 --> [n] number of SystemThreadInformation follow directly after this data structure, as defined per NumberOfThreads
    }

    /// <summary>
    /// System_Thread_Information representing a single thread of information, which follows contiguously in memory
    /// after each SystemProcessInformation
    /// 
    /// For:    NtQuerySystemInformation (w/ SystemProcessInformation)
    /// What:   Represents SYSTEM_THREAD_INFORMATION
    /// From:   .NET source code - https://referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,fd8b24cdb8931802
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    class SystemThreadInformation
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public IntPtr StartAddress;
        public IntPtr UniqueProcess;        // Process ID
        public IntPtr UniqueThread;         // Thread ID
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }

    #endregion

    #region Native APIs

    /// <summary>
    /// From: pinvoke.net/default.aspx/Structures.UNICODE_STRING
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING : IDisposable
    {
        public ushort Length;                   // ushort AKA UInt16
        public ushort MaximumLength;            // ushort AKA UInt16
        public IntPtr Buffer;

        public UNICODE_STRING(string s)
        {
            Length = (ushort)(s.Length * 2);
            MaximumLength = (ushort)(Length + 2);
            Buffer = Marshal.StringToHGlobalUni(s);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }

        public override string ToString()
        {
            return Marshal.PtrToStringUni(Buffer);
        }
    }

    [DllImport("ntdll.dll")]
    public static extern int RtlNtStatusToDosError(uint Status);

    [DllImport("ntdll.dll")]
    static extern uint NtQueryInformationProcess(
        IntPtr hProcess,
        PROCESSINFOCLASS pic,
        out PROCESS_BASIC_INFORMATION pbi,
        int cb,
        out int pSize);

    [DllImport("ntdll.dll", CharSet = CharSet.Auto)]
    public static extern uint NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformationBuffer,
        int SystemInformationLength,
        out int ReturnedSizeRequired);

    #endregion

    #region Handles

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hSnapshot);

    public const uint MAXIMUM_ALLOWED = 0x02000000;

    #endregion

    #region Process & Module Enum + Info

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Module32First(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Module32Next(IntPtr hSnapshot, ref MODULEENTRY32 lpme);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32First(IntPtr handle, ref THREADENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool Thread32Next(IntPtr handle, ref THREADENTRY32 entry);

    /// <summary>
    /// Note: th32ProcessID is IGNORED for Processes and Threads. Would need to do manual filter
    /// It is only utilized for Heap and Module enumeration
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(SnapshotFlags dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll")]
    static extern bool QueryFullProcessImageName(IntPtr hprocess, int dwFlags, StringBuilder lpExeName, out int size);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [Flags]
    public enum ProcessAccessFlags : uint
    {
        All = 0x001F0FFF,
        Terminate = 0x00000001,
        CreateThread = 0x00000002,
        VirtualMemoryOperation = 0x00000008,
        VirtualMemoryRead = 0x00000010,
        VirtualMemoryWrite = 0x00000020,
        DuplicateHandle = 0x00000040,
        CreateProcess = 0x000000080,
        SetQuota = 0x00000100,
        SetInformation = 0x00000200,
        QueryInformation = 0x00000400,
        QueryLimitedInformation = 0x00001000,
        Synchronize = 0x00100000
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESSENTRY32
    {
        public uint dwSize;

        /// <summary>
        /// Never used any longer - Always 0
        /// </summary>
        public uint cntUsage;

        /// <summary>
        /// PID
        /// </summary>
        public uint th32ProcessID;

        /// <summary>
        /// Never used any longer - Always 0
        /// </summary>
        public IntPtr th32DefaultHeapID;

        /// <summary>
        /// Never used any longer - Always 0
        /// </summary>
        public uint th32ModuleID;

        /// <summary>
        /// Thread count
        /// </summary>
        public uint cntThreads;

        /// <summary>
        /// Parent PID
        /// </summary>
        public uint th32ParentProcessID;

        /// <summary>
        /// Base priority of any threads created by this process.
        /// </summary>
        public int pcPriClassBase;

        /// <summary>
        /// Never used any longer - Always 0
        /// </summary>
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;

        // Windows XP Source toolhelp.c
        //
        // pEntry->dwSize              = sizeof(PROCESSENTRY32W);
        // pEntry->th32ProcessID       = HandleToUlong(ProcessInfo->UniqueProcessId);
        // pEntry->pcPriClassBase      = ProcessInfo->BasePriority;
        // pEntry->cntThreads          = ProcessInfo->NumberOfThreads;
        // pEntry->th32ParentProcessID = HandleToUlong(ProcessInfo->InheritedFromUniqueProcessId);
        // pEntry->cntUsage            = 0;
        // pEntry->th32DefaultHeapID   = 0;
        // pEntry->th32ModuleID        = 0;
        // pEntry->dwFlags             = 0;
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    struct MODULEENTRY32
    {
        public uint dwSize;

        /// <summary>
        /// This member is no longer used, and is always set to one.
        /// </summary>
        public uint th32ModuleID;

        /// <summary>
        /// Owning PID where this module is loaded
        /// </summary>
        public uint th32ProcessID;

        /// <summary>
        /// The load count of the module, which is not generally meaningful, and usually equal to 0xFFFF.
        /// </summary>
        public uint GlblcntUsage;

        /// <summary>
        /// The load count of the module (same as GlblcntUsage), which is not generally meaningful, and usually equal to 0xFFFF.
        /// </summary>
        public uint ProccntUsage;

        /// <summary>
        /// Remote base address
        /// </summary>
        public IntPtr modBaseAddr;

        public uint modBaseSize;

        /// <summary>
        /// Remote handle to the module
        /// </summary>
        IntPtr hModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModuleNameOnly;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szModulePath;


        // Windows XP Source toolhelp.c
        //
        //      pModule->dwSize = sizeof(MODULEENTRY32W);
        //      pModule->th32ProcessID = th32ProcessID;
        //      pModule->hModule = ModuleInfo->ImageBase;
        //      pModule->modBaseAddr = ModuleInfo->ImageBase;
        //
        //
        //      // Module Path
        //      ThpCopyAnsiToUnicode(pModule->szExePath,
        //              ModuleInfo->FullPathName,
        //              sizeof(pModule->szExePath));
        //
        //
        //      // Module name
        //      ThpCopyAnsiToUnicode(pModule->szModule,
        //                               &ModuleInfo->FullPathName[ModuleInfo->OffsetToFileName],
        //                               sizeof(pModule->szModule));
        //
        //      // Size in bytes of module starting at modBaseAddr
        //      pModule->modBaseSize = ModuleInfo->ImageSize;
        //
        //      // these are meaningless on NT
        //      pModule->th32ModuleID = 1;
        //      pModule->GlblcntUsage = ModuleInfo->LoadCount;  // will be 0xffff
        //      pModule->ProccntUsage = ModuleInfo->LoadCount;  // will be 0xffff
        //
    };

    /// <summary>
    /// From .NET source WinThreadEntry - referencesource.microsoft.com/#System/compmod/microsoft/win32/NativeMethods.cs,20a048d6318c28a7
    /// Note: Having this declare as a 'class' is WRONG. This results in Thread32First and Next returning 24 (Error_Bad_Length). Im am not sure how this works in their code. It MUST be a struct
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct THREADENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ThreadID;
        public uint th32OwnerProcessID;
        public int tpBasePri;
        public int tpDeltaPri;
        public uint dwFlags;

        // Windows XP Source toolhelp.c
        //
        // tEntry->dwSize              = sizeof(THREADENTRY32);
        // tEntry->th32ThreadID        = HandleToUlong(ThreadInfo->ClientId.UniqueThread);
        // tEntry->th32OwnerProcessID  = HandleToUlong(ThreadInfo->ClientId.UniqueProcess);
        // tEntry->tpBasePri           = ThreadInfo->BasePriority;
        // tEntry->tpDeltaPri          = 0;
        // tEntry->cntUsage            = 0;
        // tEntry->dwFlags             = 0;
    }

    const short INVALID_HANDLE_VALUE = -1;

    #endregion

    #region Thread Windows

    public delegate bool EnumThreadDelegate(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumThreadWindows(uint dwThreadId, EnumThreadDelegate lpfn, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);


    #endregion

    #region Windows Enum (via GetWindow) & Message sending

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    public const int WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;

    [DllImport("user32")]
    static extern IntPtr GetWindow(IntPtr hwnd, int wCmd);

    [DllImport("user32.dll")]
    static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    static extern IntPtr GetShellWindow();

    [DllImport("user32", EntryPoint = "GetWindowLongA")]
    static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32")]
    static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32", EntryPoint = "GetWindowTextA")]
    static extern int GetWindowText(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

    const int GWL_ID = (-12);
    const int GW_HWNDNEXT = 2;
    const int GW_CHILD = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    #endregion

    #endregion

    #region -- User-defined Data Structures: [In] : EnumInfoRequest ,  [Out] : ProcessInfo, ThreadInfo, ModuleInfo --

    /// <summary>
    /// Additional information to attempt to query for processes for loading into the ProcessInfo data structure.
    /// For best performance exclude Modules + User
    /// </summary>
    [Flags]
    public enum EnumInfoRequest
    {
        // Default info:
        //      - PID
        //      - ParentPID (available from Toolhelp and Native)
        //      - [TO DO]   - Parent process object link
        //      - Process Name
        //      - ThreadInfo list
        //      - Thread IDs
        //      - Thread count
        //
        // Additional info to request
        //      - Start Time    - Native API        TBD if can read from the process
        //                        Also populate thread Start Time here as well
        //      - HandleCount   - Native API only
        //      - Session ID    - Native API available. However can easily query without opening the process via: ProcessIdToSessionId  [TO DO]
        //      - Modules Enum  - Another toolhelp query
        //      - ProcessPath   - Open the process with QueryInfoLimited
        //      - Cmdline       - PEB read
        //      
        //
        // Token-specific info:     [TO DO]
        //      - Integrity
        //      - IsElevated
        //      - Privilege name list AND Attribute (enabled or disabled)   
        //      - Privilege count
        //      - Group list and attribute (Enabled, Disabled, Enabled default)
        //
        //
        // Non-useful information that is not included 
        //      - The supplied WinSta\Desktop, since this is only recorded in the pop
        //
        //
        // Notes:
        //      Native API = NtQuerySystemInformation(w/ SystemProcessInformation)
        //
        //      Toolhelp   = CreateToolhelp32Snapshot (w/ TH32CS_SNAPPROCESS TH32CS_SNAPTHREAD) Process32First, Next | Thread32First, Next
        //                   Toolhelp calls NtQuerySystemInformation(w/ SystemProcessInformation) underneith in toolhelp.c Windows XP SP1 source code

        // CANNOT use negative numbers here for flags, otherwise it will detect that it does have a flag value



        /// <summary>
        /// PID, Thread list (TIDs), Process name, Parent PID
        /// </summary>
        Basic = 1,


        //
        // Basic + More information
        //

        /// <summary>
        /// Requires Native API enum
        /// </summary>
        StartTime = 2,

        SessionID = 4,

        /// <summary>
        /// Will include File informaiton (Company and Description)
        /// </summary>
        Path = 8,

        Cmdline = 16,
        Modules = 32,

        /// <summary>
        /// Request to populate ProcessInfo.Integrity
        /// </summary>
        Token_Integrity = 64,

        /// <summary>
        /// Request to populate ProcessInfo.UserAndDomain
        /// </summary>
        Token_Username = 128,

        Token_PrivilegeCountOnly = 256,
        Token_Privileges = 512,

        Token_GroupsCountOnly = 1024,
        Token_Groups = 2048,

        /// <summary>
        /// Requires enumeration of Groups and Integrity to be sure
        /// </summary>
        Token_IsElevated = 4096,

        /// <summary>
        /// Maximum possible information
        /// </summary>
        Everything = Basic | StartTime | SessionID | Path | Cmdline | Modules | 
                     Token_Integrity | Token_Username | Token_PrivilegeCountOnly | Token_Privileges | Token_GroupsCountOnly | Token_Groups | Token_IsElevated,


        /// <summary>
        /// All information is included except modules which can cause a slowdown in the enumeration process, since 
        /// for each and every process, modules for that 1 process also must be included
        /// </summary>
        EverythingExceptModules = Everything & ~Modules


    }

    public class ProcessInfo
    {
        /// <summary>
        /// Is / Was the process running at the time that this information was gathered
        /// </summary>
        public bool WasProcessRunning;

        public int PID;
        public int ParentPID;               // Available via Toolhelp (how kind!) & Native API

        // Exe name, Path, Company, Description...
        public string Name = "";
        public string FullPath = "";
        public string CompanyName = "";          // IF FullPath is obtained and the file exists
        public string Description = "";          // IF FullPath is obtained and the file exists

        public int HandleCount;                 // Native API (NTQSI) only
        public DateTime StartTime;              // Native API (NTQSI) only
        public int StartOrderFromEnum;          // The process load sequence, which increments as each process is returned from the enumeration APIs

        public string Cmdline = "";         // In the PEB
        public int SessionID;               // Either Known or simple call ProcessIdToSessionId(PID)

        // Thread data is always included from PRocess enumeration, since that is included as part of NTQSI for SystemProcessInformation class ID, which toolhelp uses too for either Process or Thread snapshot
        public int ThreadCount;
        public List<ThreadInfo> Threads;
        public List<int> ThreadIDs;

        public List<ModuleInfo> Modules_Optional;

        // Token data...

        public string UserAndDomain = "";
        public ProcessInformationReading.IntegrityLevel Integrity = ProcessInformationReading.IntegrityLevel.Unknown;

        //
        // ... Token information - TO DO ...
        //

        /// <summary>
        /// Checks for High+ IL and BUILTIN\Administrators group is present
        /// </summary>
        public bool IsElevated;

        public int PrivilegeCount;
        public int GroupCount;
        public List<ProcessInformationReading.PrivilegeNameAndStatus> Privileges;
        public List<ProcessInformationReading.AccountAndAttributes> Groups;
    }



    /// <summary>
    /// From .NET source - referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/Process.cs,951923692e36b17f,references
    /// </summary>
    public class ThreadInfo
    {
        public int TID;
        public int CurrentPriority;

        /// <summary>
        /// Only available for Native API query (NTQSI). NOT with Toolhelp enum
        /// </summary>
        public DateTime StartTime;

        // For more info it requires using SystemThreadInformation instead - https://referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,fd8b24cdb8931802,references

        //public IntPtr StartAddress;
        //public System.Diagnostics.ThreadState ThreadState;
    }


    static List<ThreadInfo> ThreadInfoNTQISList_ToThreadInfoList(List<ThreadInfoNTQIS> inputList)
    {
        var ret = new List<ThreadInfo>();

        foreach(var t in inputList)
        {
            var t2 = new ThreadInfo();
            t2.TID = t.TID;
            t2.StartTime = t.StartTime;
            t2.CurrentPriority = t.Priority;
        }

        return ret;
    }

    public class ModuleInfo
    {
        public string NameOnly;
        public string FullPath;
        public int LoadSequence;
        public IntPtr PEBaseAddressRemote;
    }


    #region User-defined Data Structures for extracted data from NTQSI

    /// <summary>
    /// Subset mapping of fields from SystemProcessInformation
    /// </summary>
    public class ProcessInfoNTQSI
    {
        public int PID;
        public int ParentPID;
        public string ProcessName;
        public int SessionID;
        public DateTime StartTime;
        public int Order;
        public int HandleCount;
        public int basePriority;
        public long virtualBytes;
        public long virtualBytesPeak;
        public long workingSetPeak;
        public long workingSet;
        public long pageFileBytesPeak;
        public long pageFileBytes;
        public long privateBytes;
        public List<ThreadInfoNTQIS> ThreadInfoList;
    }

    public class ThreadInfoNTQIS
    {
        public int TID;
        public int PID;
        public int Priority;
        public IntPtr StartAddress;
        public ThreadState State;
        public DateTime StartTime;
    }

    public enum ThreadState
    {
        /// <devdoc>
        ///     The thread has been initialized, but has not started yet.
        /// </devdoc>
        Initialized,

        /// <devdoc>
        ///     The thread is in ready state.
        /// </devdoc>
        Ready,

        /// <devdoc>
        ///     The thread is running.
        /// </devdoc>
        Running,

        /// <devdoc>
        ///     The thread is in standby state.
        /// </devdoc>
        Standby,

        /// <devdoc>
        ///     The thread has exited.
        /// </devdoc>
        Terminated,

        /// <devdoc>
        ///     The thread is waiting.
        /// </devdoc>
        Wait,

        /// <devdoc>
        ///     The thread is transitioning between states.
        /// </devdoc>
        Transition,

        /// <devdoc>
        ///     The thread state is unknown.
        /// </devdoc>
        Unknown
    }

    #endregion

    #endregion

    #region Last Err

    static int _lastAPIError = 0;

    public static int LastAPIErrorCode
    {
        get
        {
            return _lastAPIError;
        }
    }

    #endregion



    #region Typical Method calls

    /// <summary>
    /// Enumerates all processes. Alias for GetAllProcesses().
    /// </summary>
    public static List<ProcessInfo> EnumProcesses(EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        bool shouldEnumModules = infoReq == EnumInfoRequest.Everything || HasFlag((int)infoReq, (int)EnumInfoRequest.Modules);

        if(HasFlag((int)infoReq, (int)EnumInfoRequest.EverythingExceptModules))
        {
            shouldEnumModules = false;
        }


        // Hard-coding to the native function for now
        return EnumProcessesAndThreads_Native_MoreInfoAndFast(infoReq);
    }

    /// <summary>
    /// Enumerates all processes. Alias for EnumProcesses().
    /// </summary>
    public static List<ProcessInfo> GetAllProcesses(EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        return EnumProcesses(infoReq);
    }

    /// <summary>
    /// Returns a list of all PIDs running on the system. Utilizes Toolhelp.
    /// </summary>
    public static List<int> GetAllProcessIDs()
    {
        var retList = new List<int>();

        int currentCount = 0;

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            var info = new PROCESSENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            bool worked = Process32First(handle, ref info);
            if (worked)
            {
                do
                {
                    try
                    {
                        retList.Add((int)info.th32ProcessID);
                        currentCount++;
                    }
                    catch (Exception ex)
                    {
                        // If its no longer running, then don't add it
                    }
                }
                while (Process32Next(handle, ref info));
            }
        }
        catch
        {
            //return retList;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(handle);
                }
                catch (Exception ex)
                {
                    // Ignore
                }
            }
        }

        return retList;
    }

    /// <summary>
    /// Utilizes Toolhelp
    /// </summary>
    public static bool IsProcessRunning(string processName)
    {
        // From: codeproject.com/Articles/12786/Writing-a-Win32-method-to-find-if-an-application-i

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            var info = new PROCESSENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            bool worked = Process32First(handle, ref info);
            if (worked)
            {
                do
                {
                    if (string.Compare(info.szExeFile, processName, true) == 0)
                    {
                        return true;
                    }
                }
                while (Process32Next(handle, ref info));
            }
        }
        catch
        {
            //return false;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(handle);
                }
                catch (Exception ex)
                {
                    // Ignore
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns all matching processes by name. Alias for GetProcessByName.
    /// </summary>
    public static List<ProcessInfo> FindProcessByName(
        string processName,
        EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        // Using the native API for this for now
        return FindProcessByName_NativeAPI(processName, infoReq);
    }

    /// <summary>
    /// Returns all matching processes by name. Alias for FindProcessByName.
    /// Replacement for System.Diagnostics.Process.GetProcessesByName, which does NOT return all processes
    /// </summary>
    public static List<ProcessInfo> GetProcessesByName(
        string processName,
        EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        return FindProcessByName(processName, infoReq);
    }

    /// <summary>
    /// Returns information about a process (encapsulated in ProcessInfo), loaded by PID. Alias for LoadProcessInfoByPID
    /// </summary>
    public static ProcessInfo GetProcessInfoByPID(
        int PID,
        EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        return LoadProcessInfoByPID(PID, infoReq);
    }



    #endregion

    #region [Private Implementation] Process enum via Native API (NTQSI)

    /// <summary>
    /// Calls NtQuerySystemInformation(w/ SystemProcessInformation) to return a list of Process information, and Thread information for each process
    /// </summary>
    static List<ProcessInfoNTQSI> EnumProcessesAndThreads_NtSystemQuery(bool getStartDatetime = false)
    {
        // Based on: social.msdn.microsoft.com/Forums/en-US/491ea0b3-3e5b-4fa2-a2c3-2f1e485aed0c/enumerate-both-32bit-and-64bit-managed-processes?forum=netfxtoolsdev
        // Which is based on the .NET source code for System.Diagnostics.Procss.GetProcesses()

        var ret = new List<ProcessInfoNTQSI>();      // Returned info
        uint nextEntryOffsetSPI = 0;            // Offset for next SystemProcessInformation in the list
        bool moreEntires = false;               // Is there a next SystemProcessInformation in the list
        int startingSize = 0x20000;             // Anything really
        int count = 0;                          // Count of processes

        const int SystemProcessInformation = 5;             // SYSTEM_INFORMATION_CLASS
        const uint InfoLengthMismatch = 0xc0000004;         // STATUS_INFO_LENGTH_MISMATCH NT Status return code. Equivalent to typical error: ERR_BAD_LENGTH(24)

        // Allocated buffer
        IntPtr allocatedMemory = Marshal.AllocHGlobal(startingSize);


        // NtQuerySystemInformation calls...
        //      Typically NTQIS is only called twice here...
        //
        int size = startingSize;
        uint retCode = 0;
        do
        {
            //
            // This is how 'ThpCreateRawSnap' (CreateToolhelp32Snapshot) operates as seen in the Windows XP SP1 source code: toolhelp.c
            //      if (dwFlags & TH32CS_SNAPPROCESS) || (dwFlags & TH32CS_SNAPTHREAD)
            //      NtQuerySystemInformation(SystemProcessInformation
            // There doesn't appear to be a way to call NtQueryInformationProcess, to just obtain threads alone
            //      
            retCode = NtQuerySystemInformation(SystemProcessInformation, allocatedMemory, size, out int required);

            int standardErr = RtlNtStatusToDosError(retCode);
            _lastAPIError = standardErr;

            if (retCode != InfoLengthMismatch)       // AKA ERR_BAD_LENGTH(24)
            {
                break;
            }

            size = required;
            allocatedMemory = Marshal.ReAllocHGlobal(allocatedMemory, new IntPtr(required));

        } while (retCode == InfoLengthMismatch);


        // All the data now exists in our process, so simply read it all...
        //      Each Process...    {   Each thread...   }
        //
        do
        {
            IntPtr memoryOffset = (IntPtr)(allocatedMemory.ToInt64() + nextEntryOffsetSPI);
            var processQuery = new SystemProcessInformation();     // Represents: SYSTEM_PROCESS_INFORMATION
            Marshal.PtrToStructure(memoryOffset, processQuery);
            count++;

            ProcessInfoNTQSI pinfo = new ProcessInfoNTQSI
            {
                PID = processQuery.UniqueProcessId.ToInt32(),
                ParentPID = processQuery.InheritedFromUniqueProcessId.ToInt32(),
                HandleCount = (int)processQuery.HandleCount,
                SessionID = (int)processQuery.SessionId,
                virtualBytes = (long)processQuery.VirtualSize,
                virtualBytesPeak = (long)processQuery.PeakVirtualSize,
                workingSetPeak = (long)processQuery.PeakWorkingSetSize,
                workingSet = (long)processQuery.WorkingSetSize,
                pageFileBytesPeak = (long)processQuery.PeakPagefileUsage,
                pageFileBytes = (long)processQuery.PagefileUsage,
                privateBytes = (long)processQuery.PrivatePageCount,
                basePriority = processQuery.BasePriority,
                StartTime = getStartDatetime ? DateTime.FromFileTime(processQuery.CreateTime) : DateTime.MinValue,      // Performance
                Order = count
            };

            if (processQuery.ProcessName.Buffer == IntPtr.Zero)
            {
                if (pinfo.PID == 4)
                {
                    pinfo.ProcessName = "System";
                }
                else if (pinfo.PID == 0)
                {
                    pinfo.ProcessName = "Idle";
                }
                else
                {
                    pinfo.ProcessName = "";
                }
            }
            else
            {
                pinfo.ProcessName = processQuery.ProcessName.ToString();

                if (pinfo.ProcessName == null)
                {
                    pinfo.ProcessName = "";
                }
            }

            ret.Add(pinfo);

            // Advance the offset to point to the memory directly after this current System_Process_Information structure
            //      in which [n] number of System_Thread_Information structures will follow
            //      https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/process.htm
            //          "
            //          Immediately following the SYSTEM_PROCESS_INFORMATION is an array of zero or more SYSTEM_THREAD_INFORMATION structures if the information class is SystemProcessInformation, else SYSTEM_EXTENDED_THREAD_INFORMATION structures. 
            //          Either way, the NumberOfThreads member tells how many.
            //          "
            //
            memoryOffset = (IntPtr)(memoryOffset.ToInt64() + Marshal.SizeOf(processQuery));
            pinfo.ThreadInfoList = new List<ThreadInfoNTQIS>();

            for (int i = 0; i < processQuery.NumberOfThreads; i++)
            {
                var threadQuery = new SystemThreadInformation();        // Represents: SYSTEM_THREAD_INFORMATION
                Marshal.PtrToStructure(memoryOffset, threadQuery);

                ThreadInfoNTQIS tinfo = new ThreadInfoNTQIS
                {
                    PID = threadQuery.UniqueProcess.ToInt32(),
                    TID = threadQuery.UniqueThread.ToInt32(),
                    Priority = threadQuery.Priority,
                    StartAddress = threadQuery.StartAddress,
                    State = (ThreadState)threadQuery.ThreadState,
                    StartTime = DateTime.FromFileTime(threadQuery.CreateTime)
                };

                pinfo.ThreadInfoList.Add(tinfo);

                // Advance the offset to be the next Thread in the list
                memoryOffset = (IntPtr)(memoryOffset.ToInt64() + Marshal.SizeOf(threadQuery));
            }

            if (processQuery.NextEntryOffset != 0)
            {
                nextEntryOffsetSPI += processQuery.NextEntryOffset;
                moreEntires = true;
            }
            else
            {
                moreEntires = false;
            }

            //int dbg = count;

        } while (moreEntires);

        Marshal.FreeHGlobal(allocatedMemory);

        return ret;
    }


    /// <summary>
    /// Calls NtQuerySystemInformation(w/ SystemProcessInformation) to return a list of Thread information for the matching PID
    /// </summary>
    static List<ThreadInfoNTQIS> EnumThreadsForProcess_NtSystemQuery(int PID)
    {
        // Based on: social.msdn.microsoft.com/Forums/en-US/491ea0b3-3e5b-4fa2-a2c3-2f1e485aed0c/enumerate-both-32bit-and-64bit-managed-processes?forum=netfxtoolsdev
        // Which is based on the .NET source code for System.Diagnostics.Procss.GetProcesses()

        var ret = new List<ThreadInfoNTQIS>();       // Returned info
        uint nextEntryOffsetSPI = 0;            // Offset for next SystemProcessInformation in the list
        bool moreEntires = false;               // Is there a next SystemProcessInformation in the list
        int startingSize = 0x20000;             // Anything really
        int count = 0;                          // Count of processes

        const int SystemProcessInformation = 5;             // SYSTEM_INFORMATION_CLASS
        const uint InfoLengthMismatch = 0xc0000004;         // STATUS_INFO_LENGTH_MISMATCH NT Status return code. Equivalent to typical error: ERR_BAD_LENGTH(24)

        // Allocated buffer
        IntPtr allocatedMemory = Marshal.AllocHGlobal(startingSize);


        // NtQuerySystemInformation calls...
        //      Typically NTQIS is only called twice here...
        //
        int size = startingSize;
        uint retCode = 0;
        do
        {
            //
            // This is how 'ThpCreateRawSnap' (CreateToolhelp32Snapshot) operates as seen in the Windows XP SP1 source code: toolhelp.c
            //      if (dwFlags & TH32CS_SNAPPROCESS) || (dwFlags & TH32CS_SNAPTHREAD)
            //      NtQuerySystemInformation(SystemProcessInformation
            // There doesn't appear to be a way to call NtQueryInformationProcess, to just obtain threads alone
            //      
            retCode = NtQuerySystemInformation(SystemProcessInformation, allocatedMemory, size, out int required);

            int standardErr = RtlNtStatusToDosError(retCode);
            _lastAPIError = standardErr;

            if (retCode != InfoLengthMismatch)       // AKA ERR_BAD_LENGTH(24)
            {
                break;
            }

            size = required;
            allocatedMemory = Marshal.ReAllocHGlobal(allocatedMemory, new IntPtr(required));

        } while (retCode == InfoLengthMismatch);


        // All the data now exists in our process, so simply read it all...
        //      Each Process...    {   Each thread...   }
        //
        do
        {
            IntPtr memoryOffset = (IntPtr)(allocatedMemory.ToInt64() + nextEntryOffsetSPI);
            var processQuery = new SystemProcessInformation();     // Represents: SYSTEM_PROCESS_INFORMATION
            Marshal.PtrToStructure(memoryOffset, processQuery);
            count++;

            if (processQuery.UniqueProcessId.ToInt32() == PID)
            {
                // Advance the offset to point to the memory directly after this current System_Process_Information structure
                //      in which [n] number of System_Thread_Information structures will follow
                //      https://www.geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/process.htm
                //          "
                //          Immediately following the SYSTEM_PROCESS_INFORMATION is an array of zero or more SYSTEM_THREAD_INFORMATION structures if the information class is SystemProcessInformation, else SYSTEM_EXTENDED_THREAD_INFORMATION structures. 
                //          Either way, the NumberOfThreads member tells how many.
                //          "
                //
                memoryOffset = (IntPtr)(memoryOffset.ToInt64() + Marshal.SizeOf(processQuery));

                for (int i = 0; i < processQuery.NumberOfThreads; i++)
                {
                    var threadQuery = new SystemThreadInformation();        // Represents: SYSTEM_THREAD_INFORMATION
                    Marshal.PtrToStructure(memoryOffset, threadQuery);

                    ThreadInfoNTQIS tinfo = new ThreadInfoNTQIS
                    {
                        PID = threadQuery.UniqueProcess.ToInt32(),
                        TID = threadQuery.UniqueThread.ToInt32(),
                        Priority = threadQuery.Priority,
                        StartAddress = threadQuery.StartAddress,
                        State = (ThreadState)threadQuery.ThreadState,
                        StartTime = DateTime.FromFileTime(threadQuery.CreateTime)
                    };

                    ret.Add(tinfo);

                    // Advance the offset to be the next Thread in the list
                    memoryOffset = (IntPtr)(memoryOffset.ToInt64() + Marshal.SizeOf(threadQuery));
                }

                return ret;
            }
            else
            {
                // Move along...

                if (processQuery.NextEntryOffset != 0)
                {
                    nextEntryOffsetSPI += processQuery.NextEntryOffset;
                    moreEntires = true;
                }
                else
                {
                    moreEntires = false;
                }
            }

        } while (moreEntires);

        Marshal.FreeHGlobal(allocatedMemory);

        return ret;
    }

    #endregion

    #region Process Enum via Toolhelp (not using Process class due to perf slowness)

    /// <summary>
    /// Uses NtQuerySystemInformation (ntdll / Native API call) to obtain the most info
    /// </summary>
    public static List<ProcessInfo> EnumProcessesAndThreads_Native_MoreInfoAndFast(EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules, int? PIDFilter = null)
    {
        var ret = new List<ProcessInfo>();

        bool convertStartTime = false;
        if(infoReq.HasFlag(EnumInfoRequest.StartTime))
        {
            convertStartTime = true;
        }

        List<ProcessInfoNTQSI> processesAndThreads = EnumProcessesAndThreads_NtSystemQuery(convertStartTime);

        bool filterSpecified = PIDFilter.HasValue;
        bool foundTargetedFilterPID_IfSupplied = false;

        foreach (ProcessInfoNTQSI pquery in processesAndThreads)
        {
            if(filterSpecified)
            {
                if(PIDFilter.HasValue && PIDFilter.Value != pquery.PID)
                {
                    // Skip to the one being sought
                    continue;
                }
                else
                {
                    foundTargetedFilterPID_IfSupplied = true;
                }
            }
            else
            {
                // Just proceed forward
            }


            List<ThreadInfo> threads = ThreadInfoNTQISList_ToThreadInfoList(pquery.ThreadInfoList);

            ProcessInfo p = LoadProcessInfo_WithKnownEnumInfo
            (
                pquery.PID,
                pquery.Order,
                infoReq,
                pquery.ProcessName,
                pquery.ParentPID,
                pquery.ThreadInfoList.Count,
                pquery.HandleCount,
                pquery.StartTime,
                pquery.SessionID,
                threads
            );

            //
            // Map the Thread info next
            //

            p.ThreadIDs = new List<int>();
            p.Threads = new List<ThreadInfo>();

            foreach (ThreadInfoNTQIS tquery in pquery.ThreadInfoList)
            {
                ThreadInfo t = new ThreadInfo();
                t.TID = tquery.TID;
                t.CurrentPriority = tquery.Priority;
                t.StartTime = tquery.StartTime;

                // Not working about State or StartAddress for now
                p.Threads.Add(t);
            }

            p.ThreadIDs = p.Threads.Select(x => x.TID).ToList();

            ret.Add(p);

            if(foundTargetedFilterPID_IfSupplied)
            {
                break;
            }
        }

        return ret;
    }

    public static List<ProcessInfo> EnumProcessesAndThreads_Toolhelp(EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        // Need:
        //      Process.GetProcessesByName() doesn't always return all processes. For example: It wont for explorer (unsure why, or if due to an integrity level issue)
        //
        // Based on:
        //      codeproject.com/Articles/12786/Writing-a-Win32-method-to-find-if-an-application-i

        const int ERROR_BAD_LENGTH = 24;

        var allProcessListRet = new List<ProcessInfo>();

        int currentCount = 0;

        IntPtr handle = IntPtr.Zero;
        try
        {

            // Include both Processes & Threads since according to the Windows XP SP1 Source in toolhelp.c, for either flag
            //    the same call is utilized to NtQuerySystemInformation (w/ SystemProcessInformation)
            // Note: The PID is ignored here for CT32S when used with Process and Thread (only utilized for Heap and Module)
            handle = CreateToolhelp32Snapshot(SnapshotFlags.Process | SnapshotFlags.Thread, 0);


            // Repetative PID data structure
            //      PID 1       Thread 1
            //      PID 1       Thread 2
            //      PID 1       Thread 3
            //      PID 2       Thread 4
            //      ...
            var allThreadList = new List<Tuple<int, ThreadInfo>>();

            var infoThreads = new THREADENTRY32();
            infoThreads.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));

            bool worked = Thread32First(handle, ref infoThreads);
            if (!worked)
            {
                _lastAPIError = Marshal.GetLastWin32Error();
            }
            else
            {
                do
                {
                    // referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,395
                    ThreadInfo threadInfo = new ThreadInfo();
                    threadInfo.TID = (int)infoThreads.th32ThreadID;
                    threadInfo.CurrentPriority = infoThreads.tpBasePri + infoThreads.tpDeltaPri;

                    var dataStructure = new Tuple<int, ThreadInfo>
                    (
                        (int)infoThreads.th32OwnerProcessID,
                        threadInfo
                    );

                    allThreadList.Add(dataStructure);

                    worked = Thread32Next(handle, ref infoThreads);
                    if (!worked)
                    {
                        _lastAPIError = Marshal.GetLastWin32Error();
                    }
                }
                while (worked);
            }



            //
            // Processes
            //

            var info = new PROCESSENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            bool worked2 = Process32First(handle, ref info);
            if (!worked2)
            {
                _lastAPIError = Marshal.GetLastWin32Error();
            }
            else
            {
                int count = 0;
                do
                {
                    // try
                    // {
                    //     // Process.GetProcessById is VERY slow -- https://github.com/dotnet/runtime/issues/20725
                    //     //Process p = Process.GetProcessById((int)info.th32ProcessID);
                    // 
                    //     Process p = Process.GetProcessById((int)info.th32ProcessID);
                    //     retList.Add(p);
                    //     currentCount++;
                    // }
                    // catch (Exception ex)
                    // {
                    //     // If its no longer running, then dont add it
                    // }

                    List<ThreadInfo> threadsForThisProcess = allThreadList.Where(x => x.Item1 == info.th32ProcessID).Select(x => x.Item2).ToList();

                    count++;

                    ProcessInfo p = LoadProcessInfo_WithKnownEnumInfo
                    (
                        (int)info.th32ProcessID,
                        count,
                        infoReq,
                        info.szExeFile,
                        (int)info.th32ParentProcessID,
                        (int)info.cntThreads,

                        // StartTime not avail with CreateToolhelp32Snapshot

                        // DO NOT do this to avoid another toolhelp snapshot
                        listOfThreads_known: threadsForThisProcess
                    );


                    // Manually build the thread list
                    p.Threads = allThreadList.Where(x => x.Item1 == p.PID).Select(x => x.Item2).ToList();
                    p.ThreadIDs = p.Threads.Select(x => x.TID).ToList();
                    p.ThreadCount = p.Threads.Count;

                    allProcessListRet.Add(p);
                    currentCount++;


                    worked2 = Process32Next(handle, ref info);
                }
                while (worked2);

            }
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(handle);
                }
                catch (Exception ex)
                {
                    // Ignore
                }
            }
        }

        return allProcessListRet;
    }

    /// <summary>
    /// Performs a process enum query to obtain otherwise unobtainable process info for 3 things:
    ///     Process name (not path)
    ///     Parent PID
    ///     Thread count    (should be obtainable regardless)
    /// </summary>
    static ProcessInfo LookupUnkownProcessInfoFromEnum(int PID, EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        // Need:
        //      Process.GetProcessesByName() doesn't always return all processes. For example: It wont for explorer (unsure why, or if due to an integrity level issue)
        //
        // Based on:
        //      codeproject.com/Articles/12786/Writing-a-Win32-method-to-find-if-an-application-i

        ProcessInfo p = EnumProcessesAndThreads_Native_MoreInfoAndFast(infoReq).Where(x => x.PID == PID).FirstOrDefault();
        if (p != null)
        {
            return p;
        }
        else
        {
            return new ProcessInfo();
        }
    }

    public static List<ProcessInfo> FindProcessByName_ToolHelp(string applicationName, EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        // Need:
        //      Process.GetProcessesByName() doesn't always return all processes. For example: It wont for explorer (unsure why, or if due to an integrity level issue)
        //
        // Based on:
        //      codeproject.com/Articles/12786/Writing-a-Win32-method-to-find-if-an-application-i

        var retList = new List<ProcessInfo>();

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            var info = new PROCESSENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

            bool worked = Process32First(handle, ref info);
            if (worked)
            {
                do
                {
                    if (string.Compare(info.szExeFile, applicationName, true) == 0)
                    {
                        // try
                        // {
                        //     // Process.GetProcessById is VERY slow -- https://github.com/dotnet/runtime/issues/20725
                        //     Process p = Process.GetProcessById((int)info.th32ProcessID);
                        //     retList.Add(p);
                        // }
                        // catch (Exception ex)
                        // {
                        //     // If its no longer running, then dont add it
                        // }

                        ProcessInfo p = LoadProcessInfo_WithKnownEnumInfo
                        (
                            (int)info.th32ProcessID,
                            0,
                            infoReq,
                            info.szExeFile,
                            (int)info.th32ParentProcessID,
                            (int)info.cntThreads
                            // StartTime not avail with CreateToolhelp32Snapshot
                        );


                        retList.Add(p);
                    }
                }
                while (Process32Next(handle, ref info));
            }
        }
        catch
        {
            //return retList;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(handle);
                }
                catch (Exception ex)
                {
                    // Ignore
                }
            }
        }

        return retList;
    }

    /// <summary>
    /// Returns a list of matching processes by names, loaded up with Thread info as well (such as for Window closing)
    /// </summary>
    public static List<ProcessInfo> FindProcessByName_NativeAPI(string applicationName, EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        // Need:
        //      Process.GetProcessesByName() doesn't always return all processes. For example: It wont for explorer (unsure why, or if due to an integrity level issue)
        //
        // Based on:
        //      codeproject.com/Articles/12786/Writing-a-Win32-method-to-find-if-an-application-i


        return EnumProcessesAndThreads_Native_MoreInfoAndFast(infoReq).Where(x => x.Name.ToLower() == applicationName.ToLower()).ToList();
    }

    #endregion

    #region Process modules and enum

    /// <summary>
    /// Toolhelp utilizes RtlQueryProcessDebugInformation (w/ RTL_QUERY_PROCESS_NONINVASIVE)
    /// </summary>
    public static List<ModuleInfo> EnumModulesForProcess(int PID)
    {
        var ret = new List<ModuleInfo>();

        // PID is only honroed for this (Module) and heap snapshot
        // See documentation + Windows XP Source code, since it uses RtlQueryProcessDebugInformation instead of NtQuerySystemInformation (w/ SystemProcessInformation) for processes and threads
        IntPtr hSnap = CreateToolhelp32Snapshot(SnapshotFlags.Module | SnapshotFlags.Module32, (uint)PID);
        if (hSnap != IntPtr.Zero)
        {
            MODULEENTRY32 info = new MODULEENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(info);

            int count = 0;
            if (Module32First(hSnap, ref info))
            {
                do
                {
                    count++;
                    ModuleInfo m = new ModuleInfo();
                    m.LoadSequence = count;
                    m.NameOnly = info.szModuleNameOnly;
                    m.FullPath = info.szModulePath;
                    m.PEBaseAddressRemote = info.modBaseAddr;

                    ret.Add(m);

                    // If more info is desired like the entrypoint -- see here - referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,450
                    // GetModuleInformation
                } while (Module32Next(hSnap, ref info));
            }
            CloseHandle(hSnap);
        }

        return ret;
    }


    /// <summary>
    /// Exe Path --> All running processes for this .exe
    /// Utilizes CreateToolhelp32Snapshot + Module32First
    /// </summary>
    public static List<ProcessInfo> GetProcessInfoByFullFileNameWin32(string path)
    {
        // From: stackoverflow.com/questions/2237628/c-sharp-process-killing

        // Example call:
        //          var test = ProcessTerminateGraceful.GetProcessByFullFileNameWin32(@"c:\windows\explorer.exe");
        //          var test2 = ProcessTerminateGraceful.GetProcessFullFileName(test[0]);
        //

        var result = new List<ProcessInfo>();

        string processName = System.IO.Path.GetFileName(path);

        foreach (var process in FindProcessByName_NativeAPI(processName))
        {
            IntPtr hModuleSnap = CreateToolhelp32Snapshot(SnapshotFlags.Module, (uint)process.PID);
            if (hModuleSnap != IntPtr.Zero)
            {
                MODULEENTRY32 me32 = new MODULEENTRY32();
                me32.dwSize = (uint)Marshal.SizeOf(me32);
                if (Module32First(hModuleSnap, ref me32))
                {
                    if (me32.szModulePath.ToLower() == path.ToLower())
                    {
                        result.Add(process);
                    }
                }
                CloseHandle(hModuleSnap);
            }
        }

        return result;
    }


    /// <summary>
    /// PID --> Full file path of the process
    /// Utilizes CreateToolhelp32Snapshot + Module32First
    /// </summary>
    public static string GetFullFileNameOfProcess_ViaModuleLookup(int PID)
    {
        IntPtr hModuleSnap = CreateToolhelp32Snapshot(SnapshotFlags.Module, (uint)PID);
        if (hModuleSnap != IntPtr.Zero)
        {
            MODULEENTRY32 me32 = new MODULEENTRY32();
            me32.dwSize = (uint)Marshal.SizeOf(me32);
            if (Module32First(hModuleSnap, ref me32))
            {
                return me32.szModulePath;
            }
            CloseHandle(hModuleSnap);
        }

        return "";
    }


    /// <summary>
    /// PID --> Full file path of the process
    /// Utilizes OpenProcess(QueryLimitedInformation) + QueryFullProcessImageName
    public static string GetFullFileNameOfProcess_ViaQueryLimited(int PID)
    {
        // From: giorgi.dev/net-framework/how-to-get-elevated-process-path-in-net/

        var buffer = new StringBuilder(1024);
        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryLimitedInformation, false, PID);

        if (hProcess != IntPtr.Zero)
        {
            try
            {
                int size = buffer.Capacity;
                if (QueryFullProcessImageName(hProcess, 0, buffer, out size))
                {
                    return buffer.ToString();
                }
            }
            finally
            {
                if (hProcess != IntPtr.Zero)
                {
                    CloseHandle(hProcess);
                }
            }
        }

        return "";
    }

    #endregion

    #region Process threads enum

    public static List<ThreadInfo> EnumThreadsForProcess_Toolhelp(int PIDFilter)
    {
        //
        // Based on .NET source Thread32Next call - referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,395
        //

        List<ThreadInfo> ret = new List<ThreadInfo>();

        // PID is IGNORED here. Must manually filter it
        IntPtr handle = CreateToolhelp32Snapshot(SnapshotFlags.Thread, 0);

        try
        {
            handle = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
            var info = new THREADENTRY32();
            info.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));

            bool worked = Thread32First(handle, ref info);
            if (!worked)
            {
                _lastAPIError = Marshal.GetLastWin32Error();
            }
            else
            {
                do
                {
                    if (info.th32OwnerProcessID == PIDFilter)
                    {
                        // referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,395
                        ThreadInfo threadInfo = new ThreadInfo();
                        threadInfo.TID = (int)info.th32ThreadID;
                        threadInfo.CurrentPriority = info.tpBasePri + info.tpDeltaPri;
                        ret.Add(threadInfo);

                        worked = Thread32Next(handle, ref info);
                        if (!worked)
                        {
                            _lastAPIError = Marshal.GetLastWin32Error();
                        }
                    }
                }
                while (worked);
            }
        }
        catch
        {
            //return ret;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(handle);
                }
                catch (Exception ex)
                {
                    // Ignore
                }
            }
        }

        return ret;
    }

    #endregion

    #region Process info loading (mapping of NTQSI or Toolhelp, to the data to be returned) - Public methods

    /// <summary>
    /// Primary method to transfer data from the 2x Process enumerator methods: NTQIS & Toolhelp
    /// </summary>
    static ProcessInfo LoadProcessInfo_WithKnownEnumInfo(
        int PID,
        int order = 0,
        EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules,
        string processName_known = "",
        int parentProcessID_known = 0,
        int threadCount_known = -1,
        int handleCount_known = -1,
        DateTime startTime_known = default(DateTime),
        int SessionID_known = -1,
        List<ThreadInfo> listOfThreads_known = null
        )
    {
        ProcessInfo ret = new ProcessInfo();

        bool WasEverythingFlagSupplied = infoReq == EnumInfoRequest.Everything || infoReq == EnumInfoRequest.EverythingExceptModules;

        // If information is known (meaning this method was called), assume TRUE.
        ret.WasProcessRunning = true;

        ret.StartOrderFromEnum = order;
        ret.PID = PID;
        ret.ParentPID = parentProcessID_known != 0 ? parentProcessID_known : ProcessInformationReading.GetParentProcessID(ret.PID);     // Should always have regardless of Toolhelp or Native and not need to read, but adding GetParentProcessID() for completeness 

        if (infoReq.HasFlag(EnumInfoRequest.Path) || WasEverythingFlagSupplied)
        {
            ret.FullPath = GetFullFileNameOfProcess_ViaQueryLimited(PID);

            if (string.IsNullOrWhiteSpace(ret.Name) && !string.IsNullOrWhiteSpace(ret.FullPath))
            {
                // Path --> Name if N/A -- Not sure how this would ever be the case, but adding for completeness
                ret.Name = !string.IsNullOrEmpty(processName_known) ? processName_known : System.IO.Path.GetFileName(ret.Name);
            }

            // Company Name + Description
            //
            try
            {
                if (System.IO.File.Exists(ret.FullPath))
                {
                    var verInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(ret.FullPath);
                    ret.CompanyName = verInfo.CompanyName;
                    ret.Description = verInfo.FileDescription;
                }
            }
            catch (Exception ex)
            {
                // ignore
            }
        }

        if (listOfThreads_known == null)
        {
            ret.Threads = EnumThreadsForProcess_Toolhelp(PID);              // Should always have it via NTQSI or Toolhelp, but in case we dont from the caller...
            ret.ThreadIDs = ret.Threads.Select(x => x.TID).ToList();
        }
        else
        {
            ret.Threads = listOfThreads_known;
            ret.ThreadIDs = ret.Threads.Select(x => x.TID).ToList();
        }

        // It's possible that we were supplied with known info from Process Enumeration via PROCESSENTRY32 ( CreateToolhelp32Snapshot, Process32First, Process32Next)
        //      which will return more available info for processes we otherwise wouldn't have access to
        ret.ThreadCount = threadCount_known > -1 ? threadCount_known : ret.Threads.Count;


        // Modules, IF desired
        //
        if (infoReq.HasFlag(EnumInfoRequest.Modules) || infoReq == EnumInfoRequest.Everything)
        {
            // Toolhelp utilizes RtlQueryProcessDebugInformation (w/ RTL_QUERY_PROCESS_NONINVASIVE)
            ret.Modules_Optional = EnumModulesForProcess(PID);
        }


        // Session ID
        //
        if (infoReq.HasFlag(EnumInfoRequest.SessionID) || WasEverythingFlagSupplied)
        {
            if (ret.SessionID <= 0)
            {
                // May actually be 0, but checking just to be sure
                ret.SessionID = SessionID_known;

                if (ret.SessionID <= 0)
                {
                    ProcessInformationReading.ProcessIdToSessionId(ret.PID, ref ret.SessionID);
                }
            }
        }


        // Start time (NTQSI only)
        //
        if (startTime_known != default(DateTime))
        {
            ret.StartTime = startTime_known;                  // < I do NOT believe this possible to read anywhere since it is within EPROCESS block, so really can only get with NTQSI >
        }


        // Handle count (NTQSI only)
        //
        ret.HandleCount = handleCount_known;



        //
        // Non-Known info not available from Process Enumeration alone:
        //

        // Cmdline  (requires ReadProcessMemory (VM_READ access to the process))
        //
        if (infoReq.HasFlag(EnumInfoRequest.Cmdline) || WasEverythingFlagSupplied)
        {
            ret.Cmdline = ProcessInformationReading.GetCommandLineForProcessID(ret.PID);
        }





        IntPtr hProcess_QueryInfo = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);

        if (hProcess_QueryInfo != IntPtr.Zero)
        {
            // Domain \ User (TOKEN_READ = (STANDARD_RIGHTS_READ | TOKEN_QUERY))
            //
            if (infoReq.HasFlag(EnumInfoRequest.Token_Username) || WasEverythingFlagSupplied)
            {
                ret.UserAndDomain = ProcessInformationReading.GetProcessUserAndDomain(hProcess_QueryInfo);
            }

            // Integrity
            //
            if (infoReq.HasFlag(EnumInfoRequest.Token_Integrity) || infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
            {
                ret.Integrity = ProcessInformationReading.GetIntegrityLevelOfProcess(hProcess_QueryInfo);
            }


            // Privileges & Count   (Requires TOKEN_READ which is greater than TOKEN_QUERY, unlike the rest of the queries)
            //
            if (infoReq.HasFlag(EnumInfoRequest.Token_Privileges) || WasEverythingFlagSupplied)
            {
                ret.Privileges = ProcessInformationReading.GetPrivilegesForProcess(hProcess_QueryInfo);
                ret.PrivilegeCount = ret.Privileges.Count;

                if (ret.PrivilegeCount == 0)
                {
                    ret.PrivilegeCount = ProcessInformationReading.GetPrivilegeCountForProcess(hProcess_QueryInfo);
                }
            }

            if (infoReq.HasFlag(EnumInfoRequest.Token_PrivilegeCountOnly) || WasEverythingFlagSupplied)
            {
                ret.PrivilegeCount = ProcessInformationReading.GetPrivilegeCountForProcess(hProcess_QueryInfo);
            }



            // Groups & Count       (only TOKEN_QUERY required)
            //
            if (infoReq.HasFlag(EnumInfoRequest.Token_Groups) || infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
            {
                ret.Groups = ProcessInformationReading.GetProcessGroupNames_AndAttributes(hProcess_QueryInfo);
                ret.GroupCount = ret.Groups.Count;

                if (ret.GroupCount == 0)
                {
                    ret.GroupCount = ProcessInformationReading.GetGroupCountForProcess(hProcess_QueryInfo);
                }
            }

            if (infoReq.HasFlag(EnumInfoRequest.Token_GroupsCountOnly) || WasEverythingFlagSupplied)
            {
                ret.GroupCount = ProcessInformationReading.GetGroupCountForProcess(hProcess_QueryInfo);
            }


            // Is Elevated (Integrity of High+ & Group check for BUILTIN\Administrators that IS enabled)
            //
            if (infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
            {
                ret.IsElevated = ProcessInformationReading.IsProcessElevated_IntegrityAndGroupCheck(hProcess_QueryInfo);
            }

            CloseHandle(hProcess_QueryInfo);
        }

        return ret;
    }



    /// <summary>
    /// A support method provided for callers to load process info
    /// This method is a subset of LoadProcessInfo_WithKnownEnumInfo()
    /// 
    /// This method will only enumerate all info if the target process can be successfully opened by the current process.
    /// Otherwise, more info may be obtainable from process enumeration which calls _WithKnownInfo variable based on the info it returns 
    ///     which is publically available to any caller on the OS (not requiring opening the process to do so)
    ///     
    /// Callers:
	///     TerminateProcessGraceful
    ///     GetProcessInfoByPID
    /// </summary>
    /// <returns></returns>
    public static ProcessInfo LoadProcessInfoByPID(int PID, EnumInfoRequest infoReq = EnumInfoRequest.EverythingExceptModules)
    {
        ProcessInfo ret = new ProcessInfo();

        bool WasEverythingFlagSupplied = infoReq == EnumInfoRequest.Everything || infoReq == EnumInfoRequest.EverythingExceptModules;

        // Test to see if this process even exists (or can even access it)
        bool isProcessValid = true;
        IntPtr hProcess = OpenProcess(MAXIMUM_ALLOWED, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            isProcessValid = false;
        }
        else
        {
            //canProceed = true;
            CloseHandle(hProcess);
        }

        ProcessInfo enumInfoFor1Process = new ProcessInfo();
        bool didQueryEnumForMoreInfoAlready = false;

        ret.PID = PID;                                                                  // PID

        if (isProcessValid)
        {
            //
            // Load all the info
            //

            // Yes, it can be opened
            ret.WasProcessRunning = true;

            ret.ParentPID = ProcessInformationReading.GetParentProcessID(PID);           // Parent ID
            if (ret.ParentPID == 0)
            {
                if (!didQueryEnumForMoreInfoAlready)
                {
                    enumInfoFor1Process = LookupUnkownProcessInfoFromEnum(PID, infoReq);
                    didQueryEnumForMoreInfoAlready = true;
                }
                ret.ParentPID = (int)enumInfoFor1Process.ParentPID;
            }

            if (infoReq.HasFlag(EnumInfoRequest.Path) || WasEverythingFlagSupplied)
            {
                ret.FullPath = GetFullFileNameOfProcess_ViaQueryLimited(PID);               // 1. Process Path via OpenProcess(QueryLimited): QueryFullProcessImageName
                if (string.IsNullOrEmpty(ret.FullPath))
                {
                    ret.FullPath = GetFullFileNameOfProcess_ViaModuleLookup(PID);           // 2. If that doesn't work try CreateToolhelp32Snapshot w/ Module32First only
                }

                // Company Name + Description
                //
                try
                {
                    if (System.IO.File.Exists(ret.FullPath))
                    {
                        var verInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(ret.FullPath);
                        ret.CompanyName = verInfo.CompanyName;
                        ret.Description = verInfo.FileDescription;
                    }
                }
                catch (Exception ex)
                {
                    // ignore
                }
            }

            // Process Name, either from the Process Path if readable; otherwise from the OS Enum Snapshot
            //
            if (string.IsNullOrEmpty(ret.FullPath)) 
            {
                if (!didQueryEnumForMoreInfoAlready)
                {
                    enumInfoFor1Process = LookupUnkownProcessInfoFromEnum(PID, infoReq);
                    didQueryEnumForMoreInfoAlready = true;
                }
                ret.Name = enumInfoFor1Process.Name;
            }
            else
            {
                ret.Name = System.IO.Path.GetFileName(ret.Name);
            }


            // Threads
            //
            if (didQueryEnumForMoreInfoAlready)
            {
                ret.Threads = enumInfoFor1Process.Threads;                              
            }
            else
            {
                ret.Threads = EnumThreadsForProcess_Toolhelp(PID);
            }

            if (ret.Threads.Count == 0)
            {
                if (!didQueryEnumForMoreInfoAlready)
                {
                    enumInfoFor1Process = LookupUnkownProcessInfoFromEnum(PID, infoReq);
                    didQueryEnumForMoreInfoAlready = true;
                }
                ret.ThreadCount = (int)enumInfoFor1Process.ThreadCount;              // Thread count. Should be able to query this regardless, since not opening the process for anything special. This too is an enum
            }
            else
            {
                ret.ThreadCount = ret.Threads.Count;
            }

            ret.ThreadIDs = ret.Threads.Select(x => x.TID).ToList();                 // Thread ID ints




            // Modules, IF desired
            //
            if (infoReq.HasFlag(EnumInfoRequest.Modules) || infoReq == EnumInfoRequest.Everything)
            {
                // Toolhelp utilizes RtlQueryProcessDebugInformation (w/ RTL_QUERY_PROCESS_NONINVASIVE)
                ret.Modules_Optional = EnumModulesForProcess(PID);                             
            }

            // Session ID
            //
            if (infoReq.HasFlag(EnumInfoRequest.SessionID) || WasEverythingFlagSupplied)
            {
                if (ret.SessionID <= 0)
                {
                    // It might actually be 0, but doesn't hurt to double check!
                    ProcessInformationReading.ProcessIdToSessionId(ret.PID, ref ret.SessionID);
                }
            }



            //
            // Process reading (ReadProcessMemory, or OpenProcessToken)
            //

            // Cmdline
            //
            if (infoReq.HasFlag(EnumInfoRequest.Cmdline) || WasEverythingFlagSupplied)
            {
                ret.Cmdline = ProcessInformationReading.GetCommandLineForProcessID(ret.PID);
            }

            // Start Time -- Wont be able to get the StartTime, unless specific did an NTQSI enumeration
            //
            //
            //ret.StartTime = EnumProcessesAndThreads_Native_MoreInfoAndFast(PID...)        // <-- dont do this -- an enum per process -- no!



            IntPtr hProcess_QueryInfo = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);

            if (hProcess_QueryInfo != IntPtr.Zero)
            {
                // Domain \ User
                //
                if (infoReq.HasFlag(EnumInfoRequest.Token_Username) || WasEverythingFlagSupplied)
                {
                    ret.UserAndDomain = ProcessInformationReading.GetProcessUserAndDomain(hProcess_QueryInfo);
                }

                // Integrity
                //
                if (infoReq.HasFlag(EnumInfoRequest.Token_Integrity) || infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
                {
                    ret.Integrity = ProcessInformationReading.GetIntegrityLevelOfProcess(hProcess_QueryInfo);
                }

                // Privileges & Count       (Requires TOKEN_READ (which is greater than TOKEN_QUERY, unlike the other queries)
                //
                if (infoReq.HasFlag(EnumInfoRequest.Token_Privileges) || WasEverythingFlagSupplied)
                {
                    ret.Privileges = ProcessInformationReading.GetPrivilegesForProcess(hProcess_QueryInfo);
                    ret.PrivilegeCount = ret.Privileges.Count;

                    if (ret.PrivilegeCount == 0)
                    {
                        ret.PrivilegeCount = ProcessInformationReading.GetPrivilegeCountForProcess(hProcess_QueryInfo);
                    }
                }

                if (infoReq.HasFlag(EnumInfoRequest.Token_PrivilegeCountOnly) || WasEverythingFlagSupplied)
                {
                    ret.PrivilegeCount = ProcessInformationReading.GetPrivilegeCountForProcess(hProcess_QueryInfo);
                }



                // Groups & Count
                //
                if (infoReq.HasFlag(EnumInfoRequest.Token_Groups) || infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
                {
                    ret.Groups = ProcessInformationReading.GetProcessGroupNames_AndAttributes(hProcess_QueryInfo);
                    ret.GroupCount = ret.Groups.Count;

                    if (ret.GroupCount == 0)
                    {
                        ret.GroupCount = ProcessInformationReading.GetGroupCountForProcess(hProcess_QueryInfo);
                    }
                }

                if (infoReq.HasFlag(EnumInfoRequest.Token_GroupsCountOnly) || WasEverythingFlagSupplied)
                {
                    ret.GroupCount = ProcessInformationReading.GetGroupCountForProcess(hProcess_QueryInfo);
                }



                // Is Elevated (Integrity of High+ & Group check for BUILTIN\Administrators that IS enabled)
                //
                if (infoReq.HasFlag(EnumInfoRequest.Token_IsElevated) || WasEverythingFlagSupplied)
                {
                    ret.IsElevated = ProcessInformationReading.IsProcessElevated_IntegrityAndGroupCheck(hProcess_QueryInfo);
                }

                CloseHandle(hProcess_QueryInfo);
            }
            
        }


        return ret;
    }




    #endregion

    #region Misc methods

    public static bool HasFlag(int flags, int flagToCheck)
    {
        return ((flags & flagToCheck) == flagToCheck);
    }

    public static int RemoveFlag(int flags, int flag)
    {
        return flags & ~flag;
    }

    public static bool HasFlag(uint flags, uint flagToCheck)
    {
        return ((flags & flagToCheck) == flagToCheck);
    }

    static bool NT_SUCCESS(uint status)
    {
        return (status & 0x80000000) == 0;
    }

    #endregion

}

#endregion

#region 3 - Process Information Querying (PEB reading)

public static class ProcessInformationReading
{
    #region -- APIs --

    #region Information Classes

    /// <summary>
    /// For CreateToolhelp32Snapshot
    /// </summary>
    [Flags]
    public enum SnapshotFlags : uint
    {
        HeapList = 1,           // TH32CS_SNAPHEAPLIST
        Process = 2,            // TH32CS_SNAPPROCESS           << Process and Thread implementation in toolhelp.c in Windows XP SP1 source code shows that Processes & Threads are done with the same NtQuerySystemInformation InfoClass call
        Thread = 4,             // TH32CS_SNAPTHREAD            << ""
        Module = 8,             // TH32CS_SNAPMODULE
        Module32 = 16,          // TH32CS_SNAPMODULE32 - Include modules from WOW64 processes
        Inherit = 0x80000000,   // 2147483648
        All = 0x0000001F        // 31
    }

    /// <summary>
    /// For GetTokenInformation
    /// 
    /// Token information and layout - "How Access Tokens Work"
    ///      technet.microsoft.com/en-us/library/cc783557(v=ws.10).aspx
    ///      (descriptions added from here)
    ///
    /// Enum From .NET Sourcecode:
    /// http://referencesource.microsoft.com/#System.ServiceModel/System/ServiceModel/Activation/ListenerUnsafeNativeMethods.cs
    /// </summary>
    public enum TOKEN_INFORMATION_CLASS : int
    {
        /// <summary>
        /// The SID for the user’s account. If the user logs on to an account on the local computer, the user’s SID is taken from the account database maintained by the local Security Accounts Manager (SAM). If the user logs on to a domain account, the SID is taken from the Object-SID property of the User object in Active Directory.
        /// </summary>
        TokenUser = 1,              // TOKEN_USER structure that contains the user account of the token. = 1, 

        /// <summary>
        ///A list of SIDs for security groups that include the user. The list also includes SIDs from the SID-History property of the User object representing the user’s account in Active Directory.
        /// </summary>
        TokenGroups,                // a TOKEN_GROUPS structure that contains the group accounts associated with the token., 

        /// <summary>
        /// A list of privileges held on the local computer by the user and by the user’s security groups.
        /// </summary>
        TokenPrivileges,            // a TOKEN_PRIVILEGES structure that contains the privileges of the token., 

        /// <summary>
        /// AKA Default Owner - The SID for the user or security group who, by default, becomes the owner of any object that the user either creates or takes ownership of.
        /// </summary>
        TokenOwner,                 // a TOKEN_OWNER structure that contains the default owner security identifier (SID) for newly created objects., 

        /// <summary>
        /// The SID for the user’s primary security group. This information is used only by the POSIX subsystem and is ignored by the rest of Windows Server 2003.
        /// </summary>
        TokenPrimaryGroup,          // a TOKEN_PRIMARY_GROUP structure that contains the default primary group SID for newly created objects., 


        /// <summary>
        /// A built-in set of permissions that the operating system applies to objects created by the user if no other access control information is available. The default DACL grants Full Control to Creator Owner and System.
        /// </summary>
        TokenDefaultDacl,           // a TOKEN_DEFAULT_DACL structure that contains the default DACL for newly created objects., 

        /// <summary>
        /// The process that caused the access token to be created, such as Session Manager, LAN Manager, or Remote Procedure Call (RPC) Server.
        /// </summary>
        TokenSource,                // a TOKEN_SOURCE structure that contains the source of the token. TOKEN_QUERY_SOURCE access is needed to retrieve this information., 

        /// <summary>
        /// A value indicating whether the access token is a primary or impersonation token.
        /// </summary>
        /// 
        TokenType,                  // a TOKEN_TYPE value that indicates whether the token is a primary or impersonation token., 

        /// <summary>
        /// A value that indicates to what extent a service can adopt the security context of a client represented by this access token.
        /// </summary>
        TokenImpersonationLevel,    // a SECURITY_IMPERSONATION_LEVEL value that indicates the impersonation level of the token. If the access token is not an impersonation token, the function fails., 

        /// <summary>
        /// Information about the access token itself. The operating system uses this information internally.
        /// </summary>
        TokenStatistics,            // a TOKEN_STATISTICS structure that contains various token statistics., 

        /// <summary>
        /// An optional list of SIDs added to an access token by a process with authority to create a restricted token. Restricting SIDs can limit a thread’s access to a level lower than what the user is allowed.
        /// </summary>
        TokenRestrictedSids,        // a TOKEN_GROUPS structure that contains the list of restricting SIDs in a restricted token., 

        /// <summary>
        /// AKA TS Session ID - A value that indicates whether the access token is associated with the Terminal Services client session.
        /// </summary>
        TokenSessionId,             // a DWORD value that indicates the Terminal Services session identifier that is associated with the token. If the token is associated with the Terminal Server console session, the session identifier is zero. If the token is associated with the Terminal Server client session, the session identifier is nonzero. In a non-Terminal Services environment, the session identifier is zero. If TokenSessionId is set with SetTokenInformation, the application must have the Act As Part Of the Operating System privilege, and the application must be enabled to set the session ID in a token.


        TokenGroupsAndPrivileges,   // a TOKEN_GROUPS_AND_PRIVILEGES structure that contains the user SID, the group accounts, the restricted SIDs, and the authentication ID associated with the token., 

        /// <summary>
        /// Reserved for internal use.
        /// </summary>
        TokenSessionReference,      // Reserved,

        /// <summary>
        /// Nonzero if the token includes the SANDBOX_INERT flag.
        /// </summary>
        TokenSandBoxInert,          // a DWORD value that is nonzero if the token includes the SANDBOX_INERT flag., 

        /// <summary>
        /// Since Windows Server 2003, used for per user auditing.
        /// </summary>
        TokenAuditPolicy,

        /// <summary>
        /// Introduced with Windows Server 2003. If the token resulted from a logon using explicit credentials, then the token will contain the ID of the logon session that created it. If the token resulted from network authentication, then this value will be zero.
        /// </summary>
        TokenOrigin,                // a TOKEN_ORIGIN value. If the token  resulted from a logon that used explicit credentials, such as passing a user, domain, and password to the  LogonUser function, then the TOKEN_ORIGIN structure will contain the ID of the logon session that created it. If the token resulted from  network authentication, such as a call to AcceptSecurityContext  or a call to LogonUser with dwLogonType set to LOGON32_LOGON_NETWORK or LOGON32_LOGON_NETWORK_CLEARTEXT, then this value will be zero.

        ///
        // Looks like these below have been Vista+ since the creation of UAC (these are not shown in the "How Access Tokens Work" article which applies only to Server 2003 and earlier)
        //

        TokenElevationType,
        TokenLinkedToken,
        TokenElevation,
        TokenHasRestrictions,
        TokenAccessInformation,
        TokenVirtualizationAllowed,
        TokenVirtualizationEnabled,
        TokenIntegrityLevel,
        TokenUIAccess,
        TokenMandatoryPolicy,
        TokenLogonSid,


        //
        // Looks like these have been added since Windows 8 and Modern UI apps (UWA)
        //

        TokenIsAppContainer,
        TokenCapabilities,
        TokenAppContainerSid,
        TokenAppContainerNumber,
        TokenUserClaimAttributes,
        TokenDeviceClaimAttributes,
        TokenRestrictedUserClaimAttributes,
        TokenRestrictedDeviceClaimAttributes,
        TokenDeviceGroups,
        TokenRestrictedDeviceGroups,


        MaxTokenInfoClass           // MaxTokenInfoClass should always be the last enum  
    }

    #endregion

    #region Public APIs

    //
    // Process Information
    //

    [DllImport("kernel32.dll")]
    public static extern int WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern int WTSQueryUserToken(uint sessionID, out IntPtr Token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern int WTSQueryUserToken(int sessionID, out IntPtr Token);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ProcessIdToSessionId(int processID, ref int sessionID);

    /// <summary>
    /// hProcess handle --> PID
    /// </summary>
    /// <param name="handle"></param>
    /// <returns></returns>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetProcessId(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetProcessIdOfThread(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = false)]
    public static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int GetCurrentProcessId();


    //
    // Process & Thread Opening
    //

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [Flags]
    public enum ProcessAccessFlags : uint
    {
        All = 0x001F0FFF,
        Terminate = 0x00000001,
        CreateThread = 0x00000002,
        VirtualMemoryOperation = 0x00000008,
        VirtualMemoryRead = 0x00000010,
        VirtualMemoryWrite = 0x00000020,
        DuplicateHandle = 0x00000040,
        CreateProcess = 0x000000080,
        SetQuota = 0x00000100,
        SetInformation = 0x00000200,
        QueryInformation = 0x00000400,
        QueryLimitedInformation = 0x00001000,
        Synchronize = 0x00100000
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, int dwThreadId);

    /// <summary>
    /// Thread-specific access rights (For OpenThread)
    /// </summary>
    [Flags]
    public enum ThreadAccess : uint
    {
        TERMINATE = (0x0001),
        SUSPEND_RESUME = (0x0002),
        GET_CONTEXT = (0x0008),
        SET_CONTEXT = (0x0010),
        SET_INFORMATION = (0x0020),
        QUERY_INFORMATION = (0x0040),
        SET_THREAD_TOKEN = (0x0080),
        IMPERSONATE = (0x0100),
        DIRECT_IMPERSONATION = (0x0200),
        THREAD_ALL_ACCESS = 0x1FFFFF
    }



    //
    // Thread Windows
    //

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    //
    // Memory RW
    //

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, int dwSize, out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll")]
    public static extern bool WriteProcessMemory(int hProcess, int lpBaseAddress, byte lpBuffer, int nSize, int lpNumberOfBytesWritten);

    #endregion

    #region Handles

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hSnapshot);

    public const uint MAXIMUM_ALLOWED = 0x02000000;

    #endregion

    #region Native APIs

    /// <summary>
    /// From: pinvoke.net/default.aspx/Structures.UNICODE_STRING
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING : IDisposable
    {
        public ushort Length;                   // ushort AKA UInt16
        public ushort MaximumLength;            // ushort AKA UInt16
        public IntPtr Buffer;

        public UNICODE_STRING(string s)
        {
            Length = (ushort)(s.Length * 2);
            MaximumLength = (ushort)(Length + 2);
            Buffer = Marshal.StringToHGlobalUni(s);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }

        public override string ToString()
        {
            return Marshal.PtrToStringUni(Buffer);
        }
    }

    [DllImport("ntdll.dll")]
    public static extern int RtlNtStatusToDosError(uint Status);

    [DllImport("ntdll.dll")]
    static extern uint NtQueryInformationProcess(
        IntPtr hProcess,
        PROCESSINFOCLASS pic,
        out PROCESS_BASIC_INFORMATION pbi,
        int cb,
        out int pSize);

    [DllImport("ntdll.dll", CharSet = CharSet.Auto)]
    public static extern uint NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformationBuffer,
        int SystemInformationLength,
        out int ReturnedSizeRequired);

    #endregion

    #region Native undocumented Information classes & structs (ntdll)

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;

        public int Size
        {
            get { return Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)); }
        }
    }

    /// <summary>
    /// MSDN published declaration - docs.microsoft.com/en-us/windows/win32/api/winternl/ns-winternl-rtl_user_process_parameters
    /// 
    /// Full declaration (which includes larger size) - nirsoft.net/kernel_struct/vista/RTL_USER_PROCESS_PARAMETERS.html
    ///         Contains things like Current Directory and ShowWindowFlags
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct RTL_USER_PROCESS_PARAMETERS
    {
        byte b0, b1, b2, b3, b4, b5, b6, b7, b8, b9, b10, b11, b12, b13, b14, b15;          // BYTE Reserved1[16];
        IntPtr ip0, ip1, ip2, ip3, ip4, ip5, ip6, ip7, ip8, ip9;                            // PVOID Reserved2[10];
        public UNICODE_STRING ImagePathName;
        public UNICODE_STRING CommandLine;
    };


    /// <summary>
    /// Full declaration - nirsoft.net/kernel_struct/vista/PEB.html
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    struct PEB
    {
        IntPtr Reserved1;
        IntPtr Reserved2;
        IntPtr Reserved3;
        public IntPtr Ldr;                  // struct PEB_LDR_DATA*
        public IntPtr ProcessParameters;    // RTL_USER_PROCESS_PARAMETERS*
        // ...
    }

    enum PROCESSINFOCLASS : int
    {
        ProcessBasicInformation = 0, // 0, q: PROCESS_BASIC_INFORMATION, PROCESS_EXTENDED_BASIC_INFORMATION
        ProcessQuotaLimits, // qs: QUOTA_LIMITS, QUOTA_LIMITS_EX
        ProcessIoCounters, // q: IO_COUNTERS
        ProcessVmCounters, // q: VM_COUNTERS, VM_COUNTERS_EX
        ProcessTimes, // q: KERNEL_USER_TIMES
        ProcessBasePriority, // s: KPRIORITY
        ProcessRaisePriority, // s: ULONG
        ProcessDebugPort, // q: HANDLE
        ProcessExceptionPort, // s: HANDLE
        ProcessAccessToken, // s: PROCESS_ACCESS_TOKEN
        ProcessLdtInformation, // 10
        ProcessLdtSize,
        ProcessDefaultHardErrorMode, // qs: ULONG
        ProcessIoPortHandlers, // (kernel-mode only)
        ProcessPooledUsageAndLimits, // q: POOLED_USAGE_AND_LIMITS
        ProcessWorkingSetWatch, // q: PROCESS_WS_WATCH_INFORMATION[]; s: void
        ProcessUserModeIOPL,
        ProcessEnableAlignmentFaultFixup, // s: BOOLEAN
        ProcessPriorityClass, // qs: PROCESS_PRIORITY_CLASS
        ProcessWx86Information,
        ProcessHandleCount, // 20, q: ULONG, PROCESS_HANDLE_INFORMATION
        ProcessAffinityMask, // s: KAFFINITY
        ProcessPriorityBoost, // qs: ULONG
        ProcessDeviceMap, // qs: PROCESS_DEVICEMAP_INFORMATION, PROCESS_DEVICEMAP_INFORMATION_EX
        ProcessSessionInformation, // q: PROCESS_SESSION_INFORMATION
        ProcessForegroundInformation, // s: PROCESS_FOREGROUND_BACKGROUND
        ProcessWow64Information, // q: ULONG_PTR
        ProcessImageFileName, // q: UNICODE_STRING
        ProcessLUIDDeviceMapsEnabled, // q: ULONG
        ProcessBreakOnTermination, // qs: ULONG
        ProcessDebugObjectHandle, // 30, q: HANDLE
        ProcessDebugFlags, // qs: ULONG
        ProcessHandleTracing, // q: PROCESS_HANDLE_TRACING_QUERY; s: size 0 disables, otherwise enables
        ProcessIoPriority, // qs: ULONG
        ProcessExecuteFlags, // qs: ULONG
        ProcessResourceManagement,
        ProcessCookie, // q: ULONG
        ProcessImageInformation, // q: SECTION_IMAGE_INFORMATION
        ProcessCycleTime, // q: PROCESS_CYCLE_TIME_INFORMATION
        ProcessPagePriority, // q: ULONG
        ProcessInstrumentationCallback, // 40
        ProcessThreadStackAllocation, // s: PROCESS_STACK_ALLOCATION_INFORMATION, PROCESS_STACK_ALLOCATION_INFORMATION_EX
        ProcessWorkingSetWatchEx, // q: PROCESS_WS_WATCH_INFORMATION_EX[]
        ProcessImageFileNameWin32, // q: UNICODE_STRING
        ProcessImageFileMapping, // q: HANDLE (input)
        ProcessAffinityUpdateMode, // qs: PROCESS_AFFINITY_UPDATE_MODE
        ProcessMemoryAllocationMode, // qs: PROCESS_MEMORY_ALLOCATION_MODE
        ProcessGroupInformation,            // q: USHORT[]
        ProcessTokenVirtualizationEnabled, // s: ULONG
        ProcessConsoleHostProcess,          // q: ULONG_PTR
        ProcessWindowInformation,           // 50, q: PROCESS_WINDOW_INFORMATION
        ProcessHandleInformation,           // q: PROCESS_HANDLE_SNAPSHOT_INFORMATION // since WIN8
        ProcessMitigationPolicy,            // s: PROCESS_MITIGATION_POLICY_INFORMATION
        ProcessDynamicFunctionTableInformation,
        ProcessHandleCheckingMode,
        ProcessKeepAliveCount,              // q: PROCESS_KEEPALIVE_COUNT_INFORMATION
        ProcessRevokeFileHandles,           // s: PROCESS_REVOKE_FILE_HANDLES_INFORMATION
        MaxProcessInfoClass
    };


    /// <summary>
    /// For:    NtQuerySystemInformation
    /// Use:    Not needed, but added for completeness / reference of available information
    /// From:   pinvoke.net/default.aspx/ntdll/SYSTEM_INFORMATION_CLASS.html
    /// Info:   geoffchappell.com/studies/windows/km/ntoskrnl/api/ex/sysinfo/class.htm
    /// </summary>
    public enum SYSTEM_INFORMATION_CLASS
    {
        SystemBasicInformation = 0x00,
        SystemProcessorInformation = 0x01,
        SystemPerformanceInformation = 0x02,
        SystemTimeOfDayInformation = 0x03,
        SystemPathInformation = 0x04,
        SystemProcessInformation = 0x05,
        SystemCallCountInformation = 0x06,
        SystemDeviceInformation = 0x07,
        SystemProcessorPerformanceInformation = 0x08,
        SystemFlagsInformation = 0x09,
        SystemCallTimeInformation = 0x0A,
        SystemModuleInformation = 0x0B,
        SystemLocksInformation = 0x0C,
        SystemStackTraceInformation = 0x0D,
        SystemPagedPoolInformation = 0x0E,
        SystemNonPagedPoolInformation = 0x0F,
        SystemHandleInformation = 0x10,
        SystemObjectInformation = 0x11,
        SystemPageFileInformation = 0x12,
        SystemVdmInstemulInformation = 0x13,
        SystemVdmBopInformation = 0x14,
        SystemFileCacheInformation = 0x15,
        SystemPoolTagInformation = 0x16,
        SystemInterruptInformation = 0x17,
        SystemDpcBehaviorInformation = 0x18,
        SystemFullMemoryInformation = 0x19,
        SystemLoadGdiDriverInformation = 0x1A,
        SystemUnloadGdiDriverInformation = 0x1B,
        SystemTimeAdjustmentInformation = 0x1C,
        SystemSummaryMemoryInformation = 0x1D,
        SystemMirrorMemoryInformation = 0x1E,
        SystemPerformanceTraceInformation = 0x1F,
        SystemObsolete0 = 0x20,
        SystemExceptionInformation = 0x21,
        SystemCrashDumpStateInformation = 0x22,
        SystemKernelDebuggerInformation = 0x23,
        SystemContextSwitchInformation = 0x24,
        SystemRegistryQuotaInformation = 0x25,
        SystemExtendServiceTableInformation = 0x26,
        SystemPrioritySeperation = 0x27,
        SystemVerifierAddDriverInformation = 0x28,
        SystemVerifierRemoveDriverInformation = 0x29,
        SystemProcessorIdleInformation = 0x2A,
        SystemLegacyDriverInformation = 0x2B,
        SystemCurrentTimeZoneInformation = 0x2C,
        SystemLookasideInformation = 0x2D,
        SystemTimeSlipNotification = 0x2E,
        SystemSessionCreate = 0x2F,
        SystemSessionDetach = 0x30,
        SystemSessionInformation = 0x31,
        SystemRangeStartInformation = 0x32,
        SystemVerifierInformation = 0x33,
        SystemVerifierThunkExtend = 0x34,
        SystemSessionProcessInformation = 0x35,
        SystemLoadGdiDriverInSystemSpace = 0x36,
        SystemNumaProcessorMap = 0x37,
        SystemPrefetcherInformation = 0x38,
        SystemExtendedProcessInformation = 0x39,
        SystemRecommendedSharedDataAlignment = 0x3A,
        SystemComPlusPackage = 0x3B,
        SystemNumaAvailableMemory = 0x3C,
        SystemProcessorPowerInformation = 0x3D,
        SystemEmulationBasicInformation = 0x3E,
        SystemEmulationProcessorInformation = 0x3F,
        SystemExtendedHandleInformation = 0x40,
        SystemLostDelayedWriteInformation = 0x41,
        SystemBigPoolInformation = 0x42,
        SystemSessionPoolTagInformation = 0x43,
        SystemSessionMappedViewInformation = 0x44,
        SystemHotpatchInformation = 0x45,
        SystemObjectSecurityMode = 0x46,
        SystemWatchdogTimerHandler = 0x47,
        SystemWatchdogTimerInformation = 0x48,
        SystemLogicalProcessorInformation = 0x49,
        SystemWow64SharedInformationObsolete = 0x4A,
        SystemRegisterFirmwareTableInformationHandler = 0x4B,
        SystemFirmwareTableInformation = 0x4C,
        SystemModuleInformationEx = 0x4D,
        SystemVerifierTriageInformation = 0x4E,
        SystemSuperfetchInformation = 0x4F,
        SystemMemoryListInformation = 0x50,
        SystemFileCacheInformationEx = 0x51,
        SystemThreadPriorityClientIdInformation = 0x52,
        SystemProcessorIdleCycleTimeInformation = 0x53,
        SystemVerifierCancellationInformation = 0x54,
        SystemProcessorPowerInformationEx = 0x55,
        SystemRefTraceInformation = 0x56,
        SystemSpecialPoolInformation = 0x57,
        SystemProcessIdInformation = 0x58,
        SystemErrorPortInformation = 0x59,
        SystemBootEnvironmentInformation = 0x5A,
        SystemHypervisorInformation = 0x5B,
        SystemVerifierInformationEx = 0x5C,
        SystemTimeZoneInformation = 0x5D,
        SystemImageFileExecutionOptionsInformation = 0x5E,
        SystemCoverageInformation = 0x5F,
        SystemPrefetchPatchInformation = 0x60,
        SystemVerifierFaultsInformation = 0x61,
        SystemSystemPartitionInformation = 0x62,
        SystemSystemDiskInformation = 0x63,
        SystemProcessorPerformanceDistribution = 0x64,
        SystemNumaProximityNodeInformation = 0x65,
        SystemDynamicTimeZoneInformation = 0x66,
        SystemCodeIntegrityInformation = 0x67,
        SystemProcessorMicrocodeUpdateInformation = 0x68,
        SystemProcessorBrandString = 0x69,
        SystemVirtualAddressInformation = 0x6A,
        SystemLogicalProcessorAndGroupInformation = 0x6B,
        SystemProcessorCycleTimeInformation = 0x6C,
        SystemStoreInformation = 0x6D,
        SystemRegistryAppendString = 0x6E,
        SystemAitSamplingValue = 0x6F,
        SystemVhdBootInformation = 0x70,
        SystemCpuQuotaInformation = 0x71,
        SystemNativeBasicInformation = 0x72,
        SystemErrorPortTimeouts = 0x73,
        SystemLowPriorityIoInformation = 0x74,
        SystemBootEntropyInformation = 0x75,
        SystemVerifierCountersInformation = 0x76,
        SystemPagedPoolInformationEx = 0x77,
        SystemSystemPtesInformationEx = 0x78,
        SystemNodeDistanceInformation = 0x79,
        SystemAcpiAuditInformation = 0x7A,
        SystemBasicPerformanceInformation = 0x7B,
        SystemQueryPerformanceCounterInformation = 0x7C,
        SystemSessionBigPoolInformation = 0x7D,
        SystemBootGraphicsInformation = 0x7E,
        SystemScrubPhysicalMemoryInformation = 0x7F,
        SystemBadPageInformation = 0x80,
        SystemProcessorProfileControlArea = 0x81,
        SystemCombinePhysicalMemoryInformation = 0x82,
        SystemEntropyInterruptTimingInformation = 0x83,
        SystemConsoleInformation = 0x84,
        SystemPlatformBinaryInformation = 0x85,
        SystemPolicyInformation = 0x86,
        SystemHypervisorProcessorCountInformation = 0x87,
        SystemDeviceDataInformation = 0x88,
        SystemDeviceDataEnumerationInformation = 0x89,
        SystemMemoryTopologyInformation = 0x8A,
        SystemMemoryChannelInformation = 0x8B,
        SystemBootLogoInformation = 0x8C,
        SystemProcessorPerformanceInformationEx = 0x8D,
        SystemCriticalProcessErrorLogInformation = 0x8E,
        SystemSecureBootPolicyInformation = 0x8F,
        SystemPageFileInformationEx = 0x90,
        SystemSecureBootInformation = 0x91,
        SystemEntropyInterruptTimingRawInformation = 0x92,
        SystemPortableWorkspaceEfiLauncherInformation = 0x93,
        SystemFullProcessInformation = 0x94,
        SystemKernelDebuggerInformationEx = 0x95,
        SystemBootMetadataInformation = 0x96,
        SystemSoftRebootInformation = 0x97,
        SystemElamCertificateInformation = 0x98,
        SystemOfflineDumpConfigInformation = 0x99,
        SystemProcessorFeaturesInformation = 0x9A,
        SystemRegistryReconciliationInformation = 0x9B,
        SystemEdidInformation = 0x9C,
        SystemManufacturingInformation = 0x9D,
        SystemEnergyEstimationConfigInformation = 0x9E,
        SystemHypervisorDetailInformation = 0x9F,
        SystemProcessorCycleStatsInformation = 0xA0,
        SystemVmGenerationCountInformation = 0xA1,
        SystemTrustedPlatformModuleInformation = 0xA2,
        SystemKernelDebuggerFlags = 0xA3,
        SystemCodeIntegrityPolicyInformation = 0xA4,
        SystemIsolatedUserModeInformation = 0xA5,
        SystemHardwareSecurityTestInterfaceResultsInformation = 0xA6,
        SystemSingleModuleInformation = 0xA7,
        SystemAllowedCpuSetsInformation = 0xA8,
        SystemDmaProtectionInformation = 0xA9,
        SystemInterruptCpuSetsInformation = 0xAA,
        SystemSecureBootPolicyFullInformation = 0xAB,
        SystemCodeIntegrityPolicyFullInformation = 0xAC,
        SystemAffinitizedInterruptProcessorInformation = 0xAD,
        SystemRootSiloInformation = 0xAE,
        SystemCpuSetInformation = 0xAF,
        SystemCpuSetTagInformation = 0xB0,
        SystemWin32WerStartCallout = 0xB1,
        SystemSecureKernelProfileInformation = 0xB2,
        SystemCodeIntegrityPlatformManifestInformation = 0xB3,
        SystemInterruptSteeringInformation = 0xB4,
        SystemSuppportedProcessorArchitectures = 0xB5,
        SystemMemoryUsageInformation = 0xB6,
        SystemCodeIntegrityCertificateInformation = 0xB7,
        SystemPhysicalMemoryInformation = 0xB8,
        SystemControlFlowTransition = 0xB9,
        SystemKernelDebuggingAllowed = 0xBA,
        SystemActivityModerationExeState = 0xBB,
        SystemActivityModerationUserSettings = 0xBC,
        SystemCodeIntegrityPoliciesFullInformation = 0xBD,
        SystemCodeIntegrityUnlockInformation = 0xBE,
        SystemIntegrityQuotaInformation = 0xBF,
        SystemFlushInformation = 0xC0,
        SystemProcessorIdleMaskInformation = 0xC1,
        SystemSecureDumpEncryptionInformation = 0xC2,
        SystemWriteConstraintInformation = 0xC3,
        SystemKernelVaShadowInformation = 0xC4,
        SystemHypervisorSharedPageInformation = 0xC5,
        SystemFirmwareBootPerformanceInformation = 0xC6,
        SystemCodeIntegrityVerificationInformation = 0xC7,
        SystemFirmwarePartitionInformation = 0xC8,
        SystemSpeculationControlInformation = 0xC9,
        SystemDmaGuardPolicyInformation = 0xCA,
        SystemEnclaveLaunchControlInformation = 0xCB,
        SystemWorkloadAllowedCpuSetsInformation = 0xCC,
        SystemCodeIntegrityUnlockModeInformation = 0xCD,
        SystemLeapSecondInformation = 0xCE,
        SystemFlags2Information = 0xCF,
        SystemSecurityModelInformation = 0xD0,
        SystemCodeIntegritySyntheticCacheInformation = 0xD1,
        MaxSystemInfoClass = 0xD2
    }

    /// <summary>
    /// For:    NtQuerySystemInformation (w/ SystemProcessInformation)
    /// What:   Represents SYSTEM_PROCESS_INFORMATION
    /// From:    .NET Source code: https://referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,0c3c811ff2002530
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    class SystemProcessInformation
    {
        // native struct defined in ntexapi.h

        /// <summary>
        /// The offset of the next SystemProcessInformation, which will travel past the previous SystemProcessInformation thread list
        /// </summary>
        public uint NextEntryOffset;

        public uint NumberOfThreads;

        public long SpareLi1;
        public long SpareLi2;
        public long SpareLi3;

        public long CreateTime;
        public long UserTime;
        public long KernelTime;

        public UNICODE_STRING ProcessName;
        public int BasePriority;
        public IntPtr UniqueProcessId;                  // int
        public IntPtr InheritedFromUniqueProcessId;     // int
        public uint HandleCount;
        public uint SessionId;
        public IntPtr PageDirectoryBase;
        public IntPtr PeakVirtualSize;  // SIZE_T
        public IntPtr VirtualSize;
        public uint PageFaultCount;

        public IntPtr PeakWorkingSetSize;
        public IntPtr WorkingSetSize;
        public IntPtr QuotaPeakPagedPoolUsage;
        public IntPtr QuotaPagedPoolUsage;
        public IntPtr QuotaPeakNonPagedPoolUsage;
        public IntPtr QuotaNonPagedPoolUsage;
        public IntPtr PagefileUsage;
        public IntPtr PeakPagefileUsage;
        public IntPtr PrivatePageCount;

        public long ReadOperationCount;
        public long WriteOperationCount;
        public long OtherOperationCount;
        public long ReadTransferCount;
        public long WriteTransferCount;
        public long OtherTransferCount;

        // 0 --> [n] number of SystemThreadInformation follow directly after this data structure, as defined per NumberOfThreads
    }

    /// <summary>
    /// System_Thread_Information representing a single thread of information, which follows contiguously in memory
    /// after each SystemProcessInformation
    /// 
    /// For:    NtQuerySystemInformation (w/ SystemProcessInformation)
    /// What:   Represents SYSTEM_THREAD_INFORMATION
    /// From:   .NET source code - https://referencesource.microsoft.com/#System/services/monitoring/system/diagnosticts/ProcessManager.cs,fd8b24cdb8931802
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    class SystemThreadInformation
    {
        public long KernelTime;
        public long UserTime;
        public long CreateTime;
        public uint WaitTime;
        public IntPtr StartAddress;
        public IntPtr UniqueProcess;        // Process ID
        public IntPtr UniqueThread;         // Thread ID
        public int Priority;
        public int BasePriority;
        public uint ContextSwitches;
        public uint ThreadState;
        public uint WaitReason;
    }

    #endregion

    #region Token opening


    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool OpenThreadToken(IntPtr ThreadHandle, uint DesiredAccess, bool CheckAccessViaProcess, out IntPtr TokenHandle);
    // Setting OpenAsSelf is what I called "CheckAccessViaProcess" - True means the access check is made against the process' user security context.    False means use the thread's user security context.


    const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    const uint TOKEN_DUPLICATE = 0x0002;
    const uint TOKEN_IMPERSONATE = 0x0004;
    const uint TOKEN_QUERY = 0x0008;
    const uint TOKEN_QUERY_SOURCE = 0x0010;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    const uint TOKEN_ADJUST_GROUPS = 0x0040;
    const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    static uint TOKEN_READ = (STANDARD_RIGHTS_READ | TOKEN_QUERY);
    static uint TOKEN_ALL_ACCESS = (STANDARD_RIGHTS_REQUIRED | TOKEN_ASSIGN_PRIMARY |
        TOKEN_DUPLICATE | TOKEN_IMPERSONATE | TOKEN_QUERY | TOKEN_QUERY_SOURCE |
        TOKEN_ADJUST_PRIVILEGES | TOKEN_ADJUST_GROUPS | TOKEN_ADJUST_DEFAULT |
        TOKEN_ADJUST_SESSIONID);

    const uint STANDARD_RIGHTS_READ = 0x00020000;

    /// <summary>
    /// Typically a part of the object-specific ALL ACCESS rights
    /// </summary>
    const uint STANDARD_RIGHTS_REQUIRED = 0x000F0000;

    #endregion

    #region Tokens and Privileges

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(IntPtr TokenHandle, TOKEN_INFORMATION_CLASS TokenInformationClass, IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);


    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true)]
    static extern bool LookupAccountSid_Ptr(
        [In, MarshalAs(UnmanagedType.LPTStr)]
        string systemName,
        IntPtr sid,
        [Out, MarshalAs(UnmanagedType.LPTStr)]
        StringBuilder name,
        ref uint cbName,
        [Out, MarshalAs(UnmanagedType.LPTStr)]
        StringBuilder referencedDomainName,
        ref uint cbReferencedDomainName,
        out SID_NAME_USE use);                                  // Could also just make this a uint instead of the structure here

    [DllImport("advapi32.dll", EntryPoint = "LookupAccountSidW", SetLastError = true)]
    static extern bool LookupAccountSid_Ptr(
        [In, MarshalAs(UnmanagedType.LPTStr)]
        string systemName,
        IntPtr sid,
        [Out, MarshalAs(UnmanagedType.LPTStr)]
        StringBuilder name,
        ref int cbName,
        [Out, MarshalAs(UnmanagedType.LPTStr)]
        StringBuilder referencedDomainName,
        ref int cbReferencedDomainName,
        out SID_NAME_USE use);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool LookupAccountName(
        string lpSystemName,
        string lpAccountName,
        [MarshalAs(UnmanagedType.LPArray)]
        byte[] Sid,
        ref int cbSid,
        StringBuilder ReferencedDomainName,
        ref int cchReferencedDomainName,
        out SID_NAME_USE peUse);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern bool LookupAccountName(
        string lpSystemName,
        string lpAccountName,
        IntPtr Sid,
        ref int cbSid,
        StringBuilder ReferencedDomainName,
        ref int cchReferencedDomainName,
        out SID_NAME_USE peUse);


    [DllImport("advapi32", SetLastError = true)]
    static extern bool ConvertSidToStringSid(IntPtr pSID, ref string pStringSid);


    /// <summary>
    /// LUID --> Privilege Name
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool LookupPrivilegeName(string lpSystemName, IntPtr lpLuid, StringBuilder lpName, ref int cchName);

    /// <summary>
    /// LUID --> Privilege Name --> LUID
    /// </summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);


    /// <summary>
    /// The PrivilegeCheck function determines whether a specified privilege is enabled in an access token.
    ///     github.com/PowerShell/PowerShell/blob/master/src/System.Management.Automation/utils/PlatformInvokes.cs
    /// </summary>
    /// <param user="tokenHandler"></param>
    /// <param user="requiredPrivileges"></param>
    /// <param user="pfResult"></param>
    /// <returns></returns>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PrivilegeCheck(IntPtr tokenHandler, ref PRIVILEGE_SET requiredPrivileges, out bool pfResult);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID
    {
        public UInt32 LowPart;
        public Int32 HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public UInt32 Attributes;
    }



    /// <summary>
    /// Needs to add Pack = 1 here for NtCreateToken
    /// 
    /// Represents a contiguous memory of privileges at the preferenced Privileges pointer/address
    /// 
    /// IMPORTANT Note for TOKEN_PRIVILEGES_ptr:
    /// Pack = 1 is VERY important here for NtCreateToken to operate correctly on x64, otherwise NtCreateToken will return ERROR_DUPLICATE_PRIVILEGES: 311, 0x137 
    /// 
    ///    The issue is that NtCreateToken sees that the buffer here in Privileges is messed up, and it IS!
    ///    Within the construction method of InitTokenPrivilegesAsParameter, a Marshal.OffsetOf('FieldName') is performed here:
    ///          IntPtr buffer = InitStructureArrayContiguous(privsStruct, (int)Marshal.OffsetOf(typeof(TOKEN_PRIVILEGES_ptr), "Privileges"), privValArray);
    ///    Marshal.OffsetOf() takes into account how the structure will be laid out in memory. With no Pack = [n] supplied, the default is to be Pack = int.
    ///    Because of Pack = Int as default, the Marshal.OffsetOf("Privileges") returns the correct value of 4 for x86. But for x64, it returns 8! This is definitely NOT correct since it should ALWAYS be 4 on any architecture
    ///       since UInt32 wont change in size (always a 32-bit integer). In fact, x86 would get messed up too, but its just "lucky" since 4 (due to Pack = int) is the size we need for the offset after UInt32.
    ///   
    ///    What Pack = [n] does is cause the fields to have a MINIMUM size, ie memory boundry. See Mastering C# structs https://www.developerfusion.com/article/84519/mastering-structs-in-c/
    ///      for more, and about struct marshaling attributes for various Windows API calls.
    ///    Basically for any structure we are going to setup manually (ex: InitStructureArrayContiguous), where we are going to use the proper convention of Marshal.OffsetOf('FieldName'), Rather than hardcoding manually,  and then be read by an API call (like NtCreateToken)
    ///      it will need to have Pack = 1 to ensure full compatability. This way each field only takes up as much space as it needs, NO more.
    ///      so 
    ///          - Char or byte = 1
    ///          - short = 2
    ///          etc
    ///          
    ///     Adding to other structures 7-24-18 to ensure correct OffsetOf calculation on x64 - SID_AND_ATTRIBUTES, TOKEN_GROUPS, TOKEN_GROUPS2
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct TOKEN_PRIVILEGES_ptr
    {
        public UInt32 PrivilegeCount;
        public IntPtr Privileges;           // LUID_AND_ATTRIBUTES[ANYSIZE_ARRAY]
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES
    {
        public UInt32 PrivilegeCount;
        public LUID Luid;
        public UInt32 Attributes;
    }

    // stackoverflow.com/questions/4349743/setting-size-of-token-privileges-luid-and-attributes-array-returned-by-gettokeni
    struct TOKEN_PRIVILEGES_1PrivArray
    {
        public UInt32 PrivilegeCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public LUID_AND_ATTRIBUTES[] Privileges;
    }

    /// <summary>
    /// DO NOT add Pack = 1 here for NtCreateToken and LsaLogonUser to operation
    /// 
    /// For TOKEN_GROUPS (which contains an array of SID_AND_ATTRIBUTES) to operate correctly under x64 for NtCreateToken or LsaLogonUser it is REQUIRED that Pack = 1 is NOT added to the structure unlike TOKEN_PRIVILEGES_Ptr.
    ///    This was discovered from testing.
    ///    The SID_AND_ATTRIBUTES structure will otherwise have a size of(sizeOfEachElement within InitStructureArrayContiguous) an incorrect value of 12, instead of an expected 16, where NtCreateToken and LsaLogonUser will fail with:
    ///    LsaLogonUser - System.AccessViolationException: 'Attempted to read or write protected memory. This is often an indication that other memory is corrupt.'
    ///    NtCreateToken - "Error: 998, 0x3E6: ERROR_NOACCESS: Invalid access to memory location"
    ///    Could be related to
    ///    https://blogs.msdn.microsoft.com/oldnewthing/20040826-00/?p=38043
    ///
    ///    So basically do NOT add Pack = 1 unless absolutely neccessary with testing the API calls first without.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_GROUPS
    {
        public int GroupCount;
        public IntPtr Groups;       // array of SID_AND_ATTRIBUTES
    }

    /// <summary>
    /// Best to utilize like this (as an array)
    /// As done here - https://gist.github.com/rgl/35b5f7d3e58aa96e598d2d6999b25bbe
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_GROUPS2
    {
        public uint GroupCount;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public SID_AND_ATTRIBUTES[] Groups;
    }


    /// <summary>
    /// DO NOT add Pack = 1 here for NtCreateToken and LsaLogonUser to operation
    /// 
    /// For TOKEN_GROUPS (which contains an array of SID_AND_ATTRIBUTES) to operate correctly under x64 for NtCreateToken or LsaLogonUser it is REQUIRED that Pack = 1 is NOT added to the structure unlike TOKEN_PRIVILEGES_Ptr.
    ///    This was discovered from testing.
    ///    The SID_AND_ATTRIBUTES structure will otherwise have a size of(sizeOfEachElement within InitStructureArrayContiguous) an incorrect value of 12, instead of an expected 16, where NtCreateToken and LsaLogonUser will fail with:
    ///    LsaLogonUser - System.AccessViolationException: 'Attempted to read or write protected memory. This is often an indication that other memory is corrupt.'
    ///    NtCreateToken - "Error: 998, 0x3E6: ERROR_NOACCESS: Invalid access to memory location"
    ///    Could be related to
    ///    https://blogs.msdn.microsoft.com/oldnewthing/20040826-00/?p=38043
    ///
    ///    So basically do NOT add Pack = 1 unless absolutely neccessary with testing the API calls first without.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SID_AND_ATTRIBUTES
    {
        public IntPtr pSID;

        /// <summary>
        /// Context specific attributes
        /// For TokenGroups token information see TokenGroupAttributes
        /// </summary>
        public TokenGroupAttributes Attributes;
    }

    /// <summary>
    /// Attributes members of the SID_AND_ATTRIBUTES structures, such as for From TOKEN_GROUPS:     msdn.microsoft.com/en-us/library/windows/desktop/aa379624(v=vs.85).aspx
    /// These attributes indicate what thie specified group (pSID in SID_AND_ATTRIBUTES) indicates
    /// 
    /// Descriptions: 
    /// GROUP_USERS_INFO_1 :            docs.microsoft.com/en-us/windows/desktop/api/lmaccess/ns-lmaccess-_group_users_info_1
    /// TOKEN_GROUPS_AND_PRIVILEGES     docs.microsoft.com/en-us/windows/desktop/api/winnt/ns-winnt-_token_groups_and_privileges
    /// 
    /// </summary>
    public enum TokenGroupAttributes : uint
    {
        Disabled = 0,                               // Just don't add SE_GROUP_ENABLED. NOTE: Don't test/check for this Disabled flag, as it will always return true for HasFlag

        /// <summary>
        /// The group/SID is mandatory, and CANNOT be disabled:
        /// 
        /// docs.microsoft.com/en-us/windows/desktop/secauthz/sid-attributes-in-an-access-token
        /// You cannot disable a group SID that has the SE_GROUP_MANDATORY attribute. You cannot use AdjustTokenGroups to disable the user SID of an access token.
        /// </summary>
        SE_GROUP_MANDATORY = 1,

        /// <summary>
        /// The group is enabled for access checks by default.
        /// </summary>
        SE_GROUP_ENABLED_BY_DEFAULT = 0x2,

        /// <summary>
        /// The group/SID is enabled for access checks.
        ///     When the system performs an access check, it checks for access-allowed and access-denied access control entries (ACEs) that apply to the SID.
        ///     A SID without this attribute is ignored during an access check unless the SE_GROUP_USE_FOR_DENY_ONLY attribute is set.
        /// </summary>
        SE_GROUP_ENABLED = 0x4,

        /// <summary>
        /// The SID identifies a group account for which the user of the token is the owner of the group, or the SID can be assigned as the owner of the token or objects.
        /// </summary>
        SE_GROUP_OWNER = 0x8,                        // Owner pSID Group name

        /// <summary>
        /// For Deny Purposes. (When this attribute is set, the SE_GROUP_ENABLED attribute must not be set.)
        /// 
        /// "SID Attributes in an Access Token"
        /// docs.microsoft.com/en-us/windows/desktop/secauthz/sid-attributes-in-an-access-token
        ///     CreateRestrictedToken can apply the SE_GROUP_USE_FOR_DENY_ONLY attribute to any SID, including the user SID and group SIDs that have the SE_GROUP_MANDATORY attribute. 
        ///     However, you cannot remove the deny-only attribute from a SID, nor can you use AdjustTokenGroups to set the SE_GROUP_ENABLED attribute on a deny-only SID.
        ///  
        /// </summary>
        SE_GROUP_USE_FOR_DENY_ONLY = 0x10,

        /// <summary>
        /// A mandatory integrity SID.      (Madatory = Cannot be modified)
        /// </summary>
        SE_GROUP_INTEGRITY = 0x20,

        /// <summary>
        /// Group is enabled for integrity level.
        /// </summary>
        SE_GROUP_INTEGRITY_ENABLED = 0x40,

        /// <summary>
        /// The SID identifies a domain-local group.
        /// </summary>
        SE_GROUP_RESOURCE = 0x20000000,

        /// <summary>
        /// The group/SID is used to identify a logon session associated with an access token.
        /// </summary>
        SE_GROUP_LOGON_ID = 0xC0000000               // The specified pSID Group name is the Logon SID
    }

    /// <summary>
    /// Attributes members of the SID_AND_ATTRIBUTES structures, such as for From TOKEN_GROUPS:     msdn.microsoft.com/en-us/library/windows/desktop/aa379624(v=vs.85).aspx
    /// These attributes indicate what thie specified group (pSID in SID_AND_ATTRIBUTES) indicates
    /// 
    /// Descriptions: 
    /// GROUP_USERS_INFO_1 :            docs.microsoft.com/en-us/windows/desktop/api/lmaccess/ns-lmaccess-_group_users_info_1
    /// TOKEN_GROUPS_AND_PRIVILEGES     docs.microsoft.com/en-us/windows/desktop/api/winnt/ns-winnt-_token_groups_and_privileges
    /// 
    /// </summary>
    public enum TokenGroupAttributes_NoDisabled : uint
    {
        /// <summary>
        /// The group/SID is mandatory, and CANNOT be disabled:
        /// 
        /// docs.microsoft.com/en-us/windows/desktop/secauthz/sid-attributes-in-an-access-token
        /// You cannot disable a group SID that has the SE_GROUP_MANDATORY attribute. You cannot use AdjustTokenGroups to disable the user SID of an access token.
        /// </summary>
        SE_GROUP_MANDATORY = 1,

        /// <summary>
        /// The group is enabled for access checks by default.
        /// </summary>
        SE_GROUP_ENABLED_BY_DEFAULT = 0x2,

        /// <summary>
        /// The group/SID is enabled for access checks.
        ///     When the system performs an access check, it checks for access-allowed and access-denied access control entries (ACEs) that apply to the SID.
        ///     A SID without this attribute is ignored during an access check unless the SE_GROUP_USE_FOR_DENY_ONLY attribute is set.
        /// </summary>
        SE_GROUP_ENABLED = 0x4,

        /// <summary>
        /// The SID identifies a group account for which the user of the token is the owner of the group, or the SID can be assigned as the owner of the token or objects.
        /// </summary>
        SE_GROUP_OWNER = 0x8,                        // Owner pSID Group name

        /// <summary>
        /// For Deny Purposes. (When this attribute is set, the SE_GROUP_ENABLED attribute must not be set.)
        /// 
        /// "SID Attributes in an Access Token"
        /// docs.microsoft.com/en-us/windows/desktop/secauthz/sid-attributes-in-an-access-token
        ///     CreateRestrictedToken can apply the SE_GROUP_USE_FOR_DENY_ONLY attribute to any SID, including the user SID and group SIDs that have the SE_GROUP_MANDATORY attribute. 
        ///     However, you cannot remove the deny-only attribute from a SID, nor can you use AdjustTokenGroups to set the SE_GROUP_ENABLED attribute on a deny-only SID.
        ///  
        /// </summary>
        SE_GROUP_USE_FOR_DENY_ONLY = 0x10,

        /// <summary>
        /// A mandatory integrity SID.      (Madatory = Cannot be modified)
        /// </summary>
        SE_GROUP_INTEGRITY = 0x20,

        /// <summary>
        /// Group is enabled for integrity level.
        /// </summary>
        SE_GROUP_INTEGRITY_ENABLED = 0x40,

        /// <summary>
        /// The SID identifies a domain-local group.
        /// </summary>
        SE_GROUP_RESOURCE = 0x20000000,

        /// <summary>
        /// The group/SID is used to identify a logon session associated with an access token.
        /// </summary>
        SE_GROUP_LOGON_ID = 0xC0000000               // The specified pSID Group name is the Logon SID
    }

    /// <summary>
    /// Custom class for populating TOKEN_GROUPS
    /// </summary>
    public class TokenGroupName
    {
        public string GroupName;
        public TokenGroupAttributes Attributes;

        public TokenGroupName(string groupName, TokenGroupAttributes attr = TokenGroupAttributes.SE_GROUP_ENABLED, bool addGroupAsDisabled = false)
        {
            GroupName = groupName;

            // Add enabled if not already there, since adding a flag would be intended to have it be enabled

            if (!addGroupAsDisabled && !HasFlag((int)attr, (int)TokenGroupAttributes.SE_GROUP_ENABLED))
            {
                attr |= TokenGroupAttributes.SE_GROUP_ENABLED;
            }

            Attributes = attr;
        }
    }




    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PRIVILEGE_SET
    {
        public uint PrivilegeCount;
        public uint Control;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    // Attributes for LUID_AND_ATTRIBUTES for Privileges
    const uint SE_PRIVILEGE_ENABLED_BY_DEFAULT = 0x00000001;
    const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    const uint SE_PRIVILEGE_REMOVED = 0x00000004;
    const uint SE_PRIVILEGE_USED_FOR_ACCESS = 0x80000000;

    public enum PrivilegeStatus
    {
        Disabled = 0,
        DefaultEnabled = 3,                                         // It actually will be 3 for Default enabled, not (int)SE_PRIVILEGE_ENABLED_BY_DEFAULT,
        Enabled = (int)SE_PRIVILEGE_ENABLED
        // Removed would mean it wouldn't exist in at all
    }

    public class PrivilegeNameAndStatus
    {
        public string Name;
        public PrivilegeStatus Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_USER_ptr
    {
        public IntPtr User; // a SID_AND_ATTRIBUTES
    }

    public enum SID_NAME_USE
    {
        User = 1,
        Group,
        Domain,
        Alias,
        WellKnownGroup,
        DeletedAccount,
        Invalid,
        Unknown,
        Computer
    }

    #endregion

    #region Tokens - Integrity

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

    // Note: If SECURITY_ATTRIBUTES parameter is used, then SECURITY_ATTRIBUTES must be initialized with a size.
    //       Example:
    //          SECURITY_ATTRIBUTES sa = new SECURITY_ATTRIBUTES();
    //          sa.nLength = Marshal.SizeOf(sa);

    [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    extern static bool DuplicateTokenEx
        (
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken
        );


    /// <summary>
    /// From: github.com/Microsoft/referencesource/blob/master/System.Data/System/Data/SQLTypes/UnsafeNativeMethods.cs
    /// Win7 enforces correct values for the _SECURITY_QUALITY_OF_SERVICE.qos member.
    /// taken from _SECURITY_IMPERSONATION_LEVEL enum definition in winnt.h
    /// </summary>
    enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,          // 0
        SecurityIdentification,     // 1
        SecurityImpersonation,      // 2
        SecurityDelegation          // 3
    }

    enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    };

    /// <summary>
    /// Windows mandatory integrity levels (Mandatory Labels)
    /// </summary>
    public enum IntegrityLevel : int
    {
        Same = -2,
        Unknown = -1,
        Untrusted = SECURITY_MANDATORY_UNTRUSTED_RID,
        Low = SECURITY_MANDATORY_LOW_RID,
        Medium = SECURITY_MANDATORY_MEDIUM_RID,
        High = SECURITY_MANDATORY_HIGH_RID,
        System = SECURITY_MANDATORY_SYSTEM_RID,
        ProtectedProcess = SECURITY_MANDATORY_PROTECTED_PROCESS_RID
    }

    // TOKEN_MANDATORY_LABEL structure
    // msdn.microsoft.com/en-us/library/windows/desktop/bb394727(v=vs.85).aspx
    //
    //      typedef struct _TOKEN_MANDATORY_LABEL {
    //           SID_AND_ATTRIBUTES Label;
    //      } TOKEN_MANDATORY_LABEL, *PTOKEN_MANDATORY_LABEL;

    public static readonly byte[] MANDATORY_LABEL_AUTHORITY = new byte[] { 0, 0, 0, 0, 0, 16 };

    // Mandatory Label SIDs (integrity levels)
    private const int SECURITY_MANDATORY_UNTRUSTED_RID = 0;
    private const int SECURITY_MANDATORY_LOW_RID = 0x1000;
    private const int SECURITY_MANDATORY_MEDIUM_RID = 0x2000;
    private const int SECURITY_MANDATORY_HIGH_RID = 0x3000;
    private const int SECURITY_MANDATORY_SYSTEM_RID = 0x4000;
    private const int SECURITY_MANDATORY_PROTECTED_PROCESS_RID = 0x5000;

    #endregion

    #endregion

    #region Last Err

    static int _lastAPIError = 0;

    public static int LastAPIErrorCode
    {
        get
        {
            return _lastAPIError;
        }
    }

    #endregion



    #region NtQueryInformationProcess methods

    /// <summary>
    /// Returns the command line stored within the process, which was supplied when this process was first run
    /// 
    /// NOTE: The PEB address is blank for when this process is running as x86 and trying to get x64 process' Command Line.
    /// NtQueryInformationProcess wont even give us the PEB address if this process is x86 and trying to read an x64 process
    ///    However the opposite is NOT true. NTQIP will gladly give x64 process callers the PEB address of x86 processes (and its read just fine)
    /// </summary>
    public static string GetCommandLineForProcessID(int PID)
    {
        var access = (uint)ProcessAccessFlags.QueryInformation | (uint)ProcessAccessFlags.VirtualMemoryRead;       // Also need to Read the process' memory
        IntPtr hProcess = OpenProcess(access, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            int size;
            PROCESS_BASIC_INFORMATION pbi;
            var status = NtQueryInformationProcess(
                hProcess, PROCESSINFOCLASS.ProcessBasicInformation,
                out pbi, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)),
                out size);

            bool success = NT_SUCCESS(status);

            _lastAPIError = RtlNtStatusToDosError((uint)status);
            if (_lastAPIError != 0)
            {
                //throw new Win32Exception(err);
            }

            if (pbi.PebBaseAddress == IntPtr.Zero)
            {
                // ERROR: Blank PEB
                return "";
            }
            else
            {
                // Read the PebBaseAddress
                var peb = ObjectFromProcessMemory<PEB>(hProcess, pbi.PebBaseAddress);

                // Get the RTL_USER_PROCESS_PARAMETERS strcture from PebBaseAddress
                var upp = ObjectFromProcessMemory<RTL_USER_PROCESS_PARAMETERS>(hProcess, peb.ProcessParameters);

                if (upp.CommandLine.Length == 0)
                {
                    return string.Empty;
                }

                // Bytes --> String
                return Encoding.Unicode.GetString(ReadProcessMemoryBytes(hProcess, upp.CommandLine.Buffer, upp.CommandLine.Length));
            }
        }
        catch (Exception ex)
        {
            //string s = ex.Source;       // Supress warning
            //Status("[e] [GetProcessCommandLine]: GetProcessCommandLine call blew up with a .NET exception: " + ex);
            return string.Empty;
        }
        finally
        {
            if (hProcess != IntPtr.Zero)
            {
                CloseHandle(hProcess);
            }
        }
    }

    /// <summary>
    /// Obtains the PID for the parent process that started the process supplied. 
    /// [!] This PID may or may NOT exist. It is queried from ProcessBasicInformation utilizing QueryLimitedInformation. (Does not requires a Process memory reading)
    /// </summary>
    /// <param name="PID">PID of the process to be read</param>
    public static int GetParentProcessID(int PID)
    {
        PROCESS_BASIC_INFORMATION pbi;
        int returnLength;

        var access = (uint)ProcessAccessFlags.QueryLimitedInformation;         // Just need ProcessBasicInformation
        IntPtr hProcess = OpenProcess(access, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            return 0;       // not returning -1 in case a typecast to (uint) is done somwhere in the future
        }

        var status = NtQueryInformationProcess(
            hProcess,
            PROCESSINFOCLASS.ProcessBasicInformation,
            out pbi,
            Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)),
            out returnLength);

        try
        {
            return pbi.InheritedFromUniqueProcessId.ToInt32();
        }
        catch (Exception ex)
        {
            // not found
            //Status("[!] [GetParentProcessID] NtQueryInformationProcess failed.");
        }

        return -1;
    }

    #endregion

    #region User (token)

    /// <summary>
    /// Returns Domain\User of a process given a token handle opened with TOKEN_QUERY
    /// </summary>
    public static string GetUserAndDomainFromToken(IntPtr hToken)
    {
        string domainRet = "";
        string userRet = "";
        bool success;

        // Request the size
        uint returnedSize;
        GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out returnedSize);

        // Allocate space, and recall the function
        // See here - goobbe.com/questions/2671574/getting-logged-on-username-from-a-service
        IntPtr infoBuffer = Marshal.AllocHGlobal((int)returnedSize);
        success = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenUser, infoBuffer, returnedSize, out returnedSize);
        if (success)
        {
            // CopyMem from IntPtr --> Struct
            TOKEN_USER_ptr tu = (TOKEN_USER_ptr)Marshal.PtrToStructure(infoBuffer, typeof(TOKEN_USER_ptr));
            SID_AND_ATTRIBUTES sidAttr = (SID_AND_ATTRIBUTES)Marshal.PtrToStructure(tu.User, typeof(SID_AND_ATTRIBUTES));

            string user, domain;
            SID_NAME_USE accountType;
            bool found = UserSIDPtrToAccountName(tu.User, out user, out domain, out accountType);
            userRet = user;
            domainRet = domain;
        }

        if (!string.IsNullOrWhiteSpace(domainRet))
        {
            domainRet += "\\";
        }

        return domainRet.Trim() + userRet.Trim();
    }

    /// <summary>
    /// Requires the process be openable with QueryInformation (0x400) and its token with TOKEN_QUERY (0x8)
    /// </summary>
    public static string GetProcessUserAndDomain(int PID)
    {
        string userRet = "";

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess != IntPtr.Zero)
        {
            bool success = OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hProcessToken);
            if (success)
            {
                userRet = GetUserAndDomainFromToken(hProcessToken);
                CloseHandle(hProcessToken);
            }

            CloseHandle(hProcess);
        }

        return userRet.Trim();
    }

    /// <summary>
    /// Requires the process be openable with QueryInformation (0x400) and its token with TOKEN_QUERY (0x8)
    /// </summary>
    public static string GetProcessUserAndDomain(IntPtr hProcess)
    {
        string userRet = "";

        bool success = OpenProcessToken(hProcess, TOKEN_QUERY, out IntPtr hProcessToken);
        if (success)
        {
            userRet = GetUserAndDomainFromToken(hProcessToken);
            CloseHandle(hProcessToken);
        }

        return userRet.Trim();
    }

    /// <summary>
    /// Slow for large number of groups due to LookupAccountSid_Ptr.
    /// 
    /// Account SID --> Account or Group name (Domain and Username returned as out params)
    /// </summary>
    public static bool UserSIDPtrToAccountName(IntPtr userSIDPtr, out string userNameRet, out string domainRet, out SID_NAME_USE accountTypeRet)
    {
        userNameRet = "";
        domainRet = "";
        accountTypeRet = SID_NAME_USE.User;

        const int NO_ERROR = 0;
        //const int ERROR_INSUFFICIENT_BUFFER = 122;
        //const int ERROR_NONE_MAPPED = 1332;

        int statusCode = NO_ERROR;

        // Use StringBuilders as our buffer
        const int startingSize = 1024;
        StringBuilder userNameBuffer = new StringBuilder();
        StringBuilder domainNameBuffer = new StringBuilder();
        userNameBuffer.EnsureCapacity(startingSize);
        domainNameBuffer.EnsureCapacity(startingSize);
        uint userNameBufferSize = (uint)userNameBuffer.Capacity;
        uint domainNameBufferSize = (uint)domainNameBuffer.Capacity;

        bool success;
        SID_NAME_USE accountType;

        if (userSIDPtr == IntPtr.Zero)
        {
            //Status("[!] [UserSIDPtrToAccountName] skipping translation. NULL SID passed in so LookupAccountSid will fail.");
            return false;
        }

        success = LookupAccountSid_Ptr(null, userSIDPtr, userNameBuffer, ref userNameBufferSize, domainNameBuffer, ref domainNameBufferSize, out accountType);
        // Will return 87 (ERROR_INVALID_PARAMETER) if the pointer to the SID isn't correct

        if (!success)
        {
            statusCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();

            //if (statusCode == ERROR_INSUFFICIENT_BUFFER || statusCode == ERROR_NONE_MAPPED)
            //{
            userNameBuffer.Clear();
            domainNameBuffer.Clear();
            userNameBufferSize += (startingSize * 2);                   // Adding to start size * 2, just in case
            domainNameBufferSize += (startingSize * 2);                 // Adding to start size * 2, just in case
            userNameBuffer.EnsureCapacity((int)userNameBufferSize);
            domainNameBuffer.EnsureCapacity((int)domainNameBufferSize);

            // Now actually make a call to LookupAccountSid that works!
            statusCode = NO_ERROR;
            success = LookupAccountSid_Ptr(null, userSIDPtr, userNameBuffer, ref userNameBufferSize, domainNameBuffer, ref domainNameBufferSize, out accountType);
            if (success == false)
            {
                statusCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }
            //}
        }

        if (!success)
        {
            // Still unsuccessful...
            // Was not able to be found, so just get the SID representation (what PH shows)
            userNameBuffer.Clear();
            domainNameBuffer.Clear();

            string sidAlone = SIDPtrToSIDString(userSIDPtr);
            userNameBuffer = new StringBuilder(sidAlone);
        }

        userNameRet = userNameBuffer.ToString().Trim();
        domainRet = domainNameBuffer.ToString().Trim();

        accountTypeRet = accountType;

        return statusCode == NO_ERROR;
    }

    /// <summary>
    /// Calls ConvertSidToStringSid
    /// "To free the returned buffer, call the LocalFree function." -- The buffer is built automatically by ConvertSidToStringSid
    /// </summary>
    /// <param name="pSID"></param>
    /// <returns></returns>
    public static string SIDPtrToSIDString(IntPtr pSID)
    {
        // Example call:
        // var sidAsString = SIDPtrToSIDString(UsernameToSIDPtr(@"NT SERVICE\TrustedInstaller"));       // Will be S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464 which is correct
        // int bp = 1;

        string s = "";

        if (pSID == IntPtr.Zero)
        {
            //Status("[!] [SIDPtrToSIDString] skipping translation. NULL SID passed in so ConvertSidToStringSid will fail.");
            return "";
        }

        bool did = ConvertSidToStringSid(pSID, ref s);

        return s;
    }

    #endregion

    #region Privileges (token)

    //
    // Privilege stats
    //

    /// <summary>
    /// Requires TOKEN_READ access right for hToken
    /// </summary>
    public static int GetPrivilegeCountForToken(IntPtr hToken)
    {
        int TokenInfLength = 0;
        GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges, IntPtr.Zero, TokenInfLength, out TokenInfLength);
        IntPtr TokenInformation = Marshal.AllocHGlobal(TokenInfLength);

        int countRet = 0;

        if (GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges, TokenInformation, TokenInfLength, out TokenInfLength))
        {
            TOKEN_PRIVILEGES_1PrivArray privilegeSet = (TOKEN_PRIVILEGES_1PrivArray)Marshal.PtrToStructure(TokenInformation, typeof(TOKEN_PRIVILEGES_1PrivArray));
            countRet = (int)privilegeSet.PrivilegeCount;
        }

        return countRet;
    }

    /// <summary>
    /// Need at least QueryInformation for the hProcess (since the TOKEN_READ access right is used for OpenProcessToken)
    /// </summary>
    /// <param name="hProcess"></param>
    /// <returns></returns>
    public static int GetPrivilegeCountForProcess(IntPtr hProcess)
    {
        int countRet = 0;
        bool success;

        if (hProcess != IntPtr.Zero)
        {
            IntPtr hProcessToken;
            success = OpenProcessToken(hProcess, TOKEN_READ, out hProcessToken);            // TOKEN_READ = Combines STANDARD_RIGHTS_READ and TOKEN_QUERY.
            if (success)
            {
                countRet = GetPrivilegeCountForToken(hProcessToken);
            }

            CloseHandle(hProcessToken);
        }

        return countRet;
    }

    public static int GetPrivilegeCountForThread(IntPtr hThread)
    {
        // TO TEST - 2-1-18

        int countRet = 0;
        bool success;

        if (hThread != IntPtr.Zero)
        {
            IntPtr hThreadToken;
            success = OpenThreadToken(hThread, TOKEN_READ, false, out hThreadToken);
            if (success)
            {
                countRet = GetPrivilegeCountForToken(hThreadToken);
            }

            CloseHandle(hThreadToken);
        }

        return countRet;
    }

    public static int GetPrivilegeCountForProcess(int PID)
    {
        int countRet = 0;

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        countRet = GetPrivilegeCountForProcess(hProcess);
        CloseHandle(hProcess);

        return countRet;
    }

    public static int GetPrivilegeCountForThread(int TID)
    {
        // TO TEST - 2-1-18

        int countRet = 0;
        IntPtr hThread = OpenThread(ThreadAccess.QUERY_INFORMATION, false, (uint)TID);
        if (hThread != IntPtr.Zero)
        {
            countRet = GetPrivilegeCountForThread(hThread);
            CloseHandle(hThread);
        }

        return countRet;
    }


    static List<LUIDToPrvilegeName> cachedLUIDsToPrvilegeNames = new List<LUIDToPrvilegeName>();
    class LUIDToPrvilegeName
    {
        public LUID Luid;
        public string PrivilegeName;
    }

    /// <summary>
    /// An LUID caching lookup for LookupPrivilegeName since this API is slow, and is a hot path as per LogonUserSuper \ ImpersonateSystemProcess \ WtsEnumerateProcesses, whereby this is called for every privilege in every process
    /// Performance booster for LogonUserSuper - 6-1-18 - Saves Seconds!
    /// </summary>
    /// <param name="currLuid"></param>
    /// <returns></returns>
    static PrivilegeNameAndStatus LookupPrivilegeName_Cached(LUID_AND_ATTRIBUTES currLuid)
    {
        PrivilegeNameAndStatus privInfoRet = new PrivilegeNameAndStatus();

        var foundLuidPrivName = cachedLUIDsToPrvilegeNames.FirstOrDefault(x => x.Luid.HighPart == currLuid.Luid.HighPart && x.Luid.LowPart == currLuid.Luid.LowPart);
        if (foundLuidPrivName != null)
        {
            //
            // This privilege has been encountered already
            //

            privInfoRet.Name = foundLuidPrivName.PrivilegeName;
            privInfoRet.Status = (PrivilegeStatus)currLuid.Attributes;
        }
        else
        {
            //
            // This privilege hasn't been encountered yet
            //

            System.Text.StringBuilder nameBuffer = new System.Text.StringBuilder();
            int iLuidNameLen = 0; //Holds the length of structure we will be receiving LookupPrivilagename
            IntPtr ipLuid = Marshal.AllocHGlobal(Marshal.SizeOf(currLuid.Luid)); //Allocate a block of memory large enough to hold the structure
            Marshal.StructureToPtr(currLuid.Luid, ipLuid, true); //Write the structure into the reserved space in unmanaged memory

            // 1st call - Get size to Allocate buffer
            LookupPrivilegeName(null, ipLuid, null, ref iLuidNameLen);
            nameBuffer.EnsureCapacity(iLuidNameLen + 1);

            // 2nd call again - get contents
            if (LookupPrivilegeName(null, ipLuid, nameBuffer, ref iLuidNameLen))
            {
                // Free up the reserved space in unmanaged memory (Should be done any time AllocHGlobal is used)
                Marshal.FreeHGlobal(ipLuid);

                // Map LUID --> Privilege information
                PrivilegeNameAndStatus privInfo = new PrivilegeNameAndStatus();
                privInfo.Name = nameBuffer.ToString();
                privInfo.Status = (PrivilegeStatus)currLuid.Attributes;

                // Set the return value
                privInfoRet = privInfo;

                //
                // Store it in the cache
                //
                cachedLUIDsToPrvilegeNames.Add(new LUIDToPrvilegeName { Luid = currLuid.Luid, PrivilegeName = privInfo.Name });
            }
        }

        return privInfoRet;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="hToken">A token handle opened with TOKEN_READ</param>
    /// <returns></returns>
    static List<PrivilegeNameAndStatus> GetPrivilegesForToken(IntPtr hToken)
    {
        // Based on:
        //      stackoverflow.com/questions/4349743/setting-size-of-token-privileges-luid-and-attributes-array-returned-by-gettokeni
        //
        var retList = new List<PrivilegeNameAndStatus>();


        if (hToken != IntPtr.Zero)
        {
            int sizeNeeded = 0;

            // First call to GetTokenInformation returns the length of the structure that will be returned on the next call
            GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges, IntPtr.Zero, sizeNeeded, out sizeNeeded);

            // Allocate a block of memory large enough to hold the expected structure
            IntPtr pTokenInfoBuffer = Marshal.AllocHGlobal(sizeNeeded);

            // pTokenInfoBuffer holds the starting location readable as an integer
            // you can view the memory location using Ctrl-Alt-M,1 or Debug->Windows->Memory->Memory1 in Visual Studio
            // and pasting the value of pTokenInfoBuffer into the search box (it's still empty right now though)
            if (GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenPrivileges, pTokenInfoBuffer, sizeNeeded, out sizeNeeded))
            {
                // If GetTokenInformation doesn't return false then the structure should be sitting in the space reserved by pTokenInfoBuffer at this point

                // What was returned is a structure of type TOKEN_PRIVILEGES which has two values, a UInt32 followed by an array
                // of LUID_AND_ATTRIBUTES structures. Because we know what to expect and we know the order to expect it we can section
                // off the memory into marshalled structures and do some math to figure out where to start our next marshal

                uint nPrivCount = (uint)Marshal.PtrToStructure(pTokenInfoBuffer, typeof(uint));                     // Get the count

                // lets create the structure we should have had in the first place
                LUID_AND_ATTRIBUTES[] luidArray = new LUID_AND_ATTRIBUTES[nPrivCount];                              // initialize an array to the right size
                LUID_AND_ATTRIBUTES luid = new LUID_AND_ATTRIBUTES();

                // pLuid will hold our new location to read from by taking the last pointer plus the size of the last structure read
                IntPtr pLuid = new IntPtr(pTokenInfoBuffer.ToInt64() + sizeof(uint));                               // first luid pointer
                luid = (LUID_AND_ATTRIBUTES)Marshal.PtrToStructure(pLuid, typeof(LUID_AND_ATTRIBUTES));             // Read the memory location
                if (luidArray.Length > 0)
                {
                    luidArray[0] = luid;
                }
                // Add it to the array

                //After getting our first structure we can loop through the rest since they will all be the same
                for (int i = 1; i < nPrivCount; ++i)
                {
                    pLuid = new IntPtr(pLuid.ToInt64() + Marshal.SizeOf(luid));                                     // Update the starting point in pLuid
                    luid = (LUID_AND_ATTRIBUTES)Marshal.PtrToStructure(pLuid, typeof(LUID_AND_ATTRIBUTES));         // Read the memory location
                    luidArray[i] = luid;                                                                            // Add it to the array
                }

                TOKEN_PRIVILEGES_1PrivArray cPrivilegeSet = new TOKEN_PRIVILEGES_1PrivArray();
                cPrivilegeSet.PrivilegeCount = nPrivCount;
                cPrivilegeSet.Privileges = luidArray;

                // DBG -- now we have what we should have had to begin with
                //Console.WriteLine("Privilege Count: {0}", cPrivilegeSet.PrivilegeCount.ToString());

                // This loops through the LUID_AND_ATTRIBUTES array and resolves the LUID names with a
                // call to LookupPrivilegeName which requires us to first convert our managed structure into an unmanaged one
                // so we get to see what it looks like to do it backwards
                foreach (LUID_AND_ATTRIBUTES currLuid in cPrivilegeSet.Privileges)
                {

                    // Map LUID --> Privilege information
                    PrivilegeNameAndStatus info = LookupPrivilegeName_Cached(currLuid);

                    if (!string.IsNullOrWhiteSpace(info.Name))
                    {
                        retList.Add(info);
                    }

                }

                // Free up the reserved space in unmanaged memory (Should be done any time AllocHGlobal is used)
                Marshal.FreeHGlobal(pTokenInfoBuffer);
            }

            // DBG
            //foreach (var priv in retList)
            //{
            //    Console.WriteLine("[{0}] : {1}", priv.Status.ToString(), priv.Name);
            //}
        }

        return retList;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="hProcess">A process handle opened with ProcessAccessFlags.QueryInformation</param>
    /// <returns></returns>
    public static List<PrivilegeNameAndStatus> GetPrivilegesForProcess(IntPtr hProcess)
    {
        var retList = new List<PrivilegeNameAndStatus>();
        bool success;

        if (hProcess != IntPtr.Zero)
        {
            IntPtr hProcessToken;
            success = OpenProcessToken(hProcess, TOKEN_READ, out hProcessToken);
            if (success)
            {
                retList = GetPrivilegesForToken(hProcessToken);
            }

            CloseHandle(hProcessToken);
        }

        return retList;
    }

    /// <summary>
    /// 
    /// </summary>
    public static List<PrivilegeNameAndStatus> GetPrivilegesForProcess(int PID)
    {
        var retList = new List<PrivilegeNameAndStatus>();
        bool success;

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess != IntPtr.Zero)
        {
            IntPtr hProcessToken;
            success = OpenProcessToken(hProcess, TOKEN_READ, out hProcessToken);
            if (success)
            {
                retList = GetPrivilegesForToken(hProcessToken);
            }

            CloseHandle(hProcess);
            CloseHandle(hProcessToken);
        }

        return retList;
    }

    /// <summary>
    /// 
    /// </summary>
    public static List<PrivilegeNameAndStatus> GetPrivilegesForThread(int TID)
    {
        // TO TEST - 2-1-18

        var retList = new List<PrivilegeNameAndStatus>();
        bool success;

        IntPtr hThread = OpenThread(ThreadAccess.QUERY_INFORMATION, false, (uint)TID);
        if (hThread != IntPtr.Zero)
        {
            IntPtr hThreadToken;
            success = OpenThreadToken(hThread, TOKEN_READ, false, out hThreadToken);
            if (success)
            {
                retList = GetPrivilegesForToken(hThreadToken);
            }

            CloseHandle(hThread);
            CloseHandle(hThreadToken);
        }


        return retList;
    }


    /// <summary>
    /// Checks to see if the token has the desired privilege
    /// </summary>
    /// <param name="privilegeName"></param>
    /// <returns></returns>
    static bool DoesPrivilegeExistForToken(IntPtr hToken, string privilegeName)
    {
        List<PrivilegeNameAndStatus> privs = GetPrivilegesForToken(hToken);
        PrivilegeNameAndStatus privInfo = privs.FirstOrDefault(x => x.Name.ToLower() == privilegeName.ToLower());
        if (privInfo != null)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    static string GetPrivilegesHeldForToken_ToString(IntPtr hToken)
    {
        string csvRet = "";

        int count = 0;
        var privList = GetPrivilegesForToken(hToken);

        //Status("[GetPrivilegesHeldForToken_ToString]: Number privileges returned: " + privList.Count, ShowStatusWhen.VerboseMode);

        privList = privList.OrderBy(x => x.Name).ToList();          // Sort alphabetically
        foreach (var p in privList)
        {
            count++;

            string pName = p.Name;
            string nameMod = pName.Replace("Privilege", "");

            if (nameMod.Length > 2)
            {
                nameMod = nameMod.Substring(2);         // Remove index 0 and 1 for removing "Se" from the start
            }

            string status = "";
            if (p.Status == PrivilegeStatus.Disabled)
            {
                status = "0";
            }
            else if (p.Status == PrivilegeStatus.Enabled || p.Status == PrivilegeStatus.DefaultEnabled)
            {
                status = "1";
            }

            csvRet += string.Format("{0}:{1}", nameMod, status);

            if (count < privList.Count)
            {
                csvRet += "  ";
            }
        }

        return csvRet;
    }

    #endregion

    #region Groups (token)

    #region Account + AccountAndAttributes representing a single token Group

    public class Account
    {
        public string Sid { get; set; }
        public string Domain { get; set; }
        public string Name { get; set; }

        /// <summary>
        /// SID_NAME_USE returned from LookupAccountSidA 
        ///        SID_NAME_USE - docs.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-sid_name_use
        /// </summary>
        public string Type { get; set; }

        public override string ToString()
        {
            return string.Format(
                string.IsNullOrEmpty(Domain)
                    ? @"{1} ({2}; {3})"
                    : @"{0}\{1} ({2}; {3})",
                Domain,
                Name,
                Sid,
                Type);
        }
    }

    public class AccountAndAttributes
    {
        // From: whoami.ps1
        // gist.github.com/rgl/35b5f7d3e58aa96e598d2d6999b25bbe

        /// <summary>
        /// SID string, Domain & User
        /// </summary>
        public Account Account { get; set; }

        /// <summary>
        /// List of TokenGroupAttributes enum flags as string
        /// </summary>
        public string[] AttributesNames { get; set; }

        public uint AttributesFlags { get; set; }

        public override string ToString()
        {
            return string.Format("{0} {1}", Account.ToString(), string.Join("|", AttributesNames));
        }
    }

    #endregion

    /// <summary>
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<string> GetProcessGroupNames(int PID)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            return new List<string>();
        }
        else
        {
            var list = GetProcessGroupNames(hProcess);
            CloseHandle(hToken);
            return list;
        }
    }

    /// <summary>
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<string> GetProcessGroupNames(IntPtr hProcess)
    {
        IntPtr hToken = IntPtr.Zero;
        bool opened = OpenProcessToken(hProcess, TOKEN_QUERY, out hToken);

        if (hToken == IntPtr.Zero || !opened)
        {
            return new List<string>();
        }
        else
        {
            var list = GetGroupNamesFromToken(hToken);

            CloseHandle(hToken);

            return list;
        }
    }

    /// <summary>
    /// Returns the Domain\User of each group in the specified token
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<string> GetGroupNamesFromToken(IntPtr hToken)
    {
        List<string> groupsRet = new List<string>();
        bool success;

        // Request the size
        uint returnedSize;
        GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenGroups, IntPtr.Zero, 0, out returnedSize);

        // Allocate space, and recall the function
        // See here - http://goobbe.com/questions/2671574/getting-logged-on-username-from-a-service
        IntPtr infoBuffer = Marshal.AllocHGlobal((int)returnedSize);
        success = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenGroups, infoBuffer, returnedSize, out returnedSize);
        if (success)
        {
            TOKEN_GROUPS2 groups = (TOKEN_GROUPS2)Marshal.PtrToStructure(infoBuffer, typeof(TOKEN_GROUPS2));

            // Array in memory --> Array
            var sidArray = new SID_AND_ATTRIBUTES[groups.GroupCount];
            PtrToStructureArray("Groups", infoBuffer, sidArray);

            foreach (var s in sidArray)
            {
                string retUser = "";
                string retDomain = "";
                SID_NAME_USE use;
                bool successfullyTranslated = UserSIDPtrToAccountName(s.pSID, out retUser, out retDomain, out use);

                if (!successfullyTranslated)
                {
                    // Fallback failcase -- cannot translate -- such as a cached group in the current token when the domain is not connected
                    //      ConvertSidToStringSid will work just fine (like in PH)

                    // ConvertSidToStringSid
                    string stringSID = SIDPtrToSIDString(s.pSID);
                    bool successfullyConverted = stringSID != "";

                    if (successfullyConverted)
                    {
                        groupsRet.Add(stringSID);
                    }
                }
                else
                {
                    string domainAndSlash = !string.IsNullOrWhiteSpace(retDomain.ToString().Trim()) ? retDomain.ToString().Trim() + "\\" : "";
                    groupsRet.Add(domainAndSlash + retUser.ToString().Trim());
                }

            }

            Marshal.FreeHGlobal(infoBuffer);
        }

        return groupsRet;
    }




    /// <summary>
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<AccountAndAttributes> GetProcessGroupNames_AndAttributes(int PID)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            return new List<AccountAndAttributes>();
        }
        else
        {
            var list = GetProcessGroupNames_AndAttributes(hProcess);
            CloseHandle(hToken);
            return list;
        }
    }

    /// <summary>
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<AccountAndAttributes> GetProcessGroupNames_AndAttributes(IntPtr hProcess)
    {
        IntPtr hToken = IntPtr.Zero;
        bool opened = OpenProcessToken(hProcess, TOKEN_QUERY, out hToken);

        if (hToken == IntPtr.Zero || !opened)
        {
            return new List<AccountAndAttributes>();
        }
        else
        {
            var list = GetProcessGroupNames_AndAttributes_FromToken(hToken);

            CloseHandle(hToken);

            return list;
        }
    }

    /// <summary>
    /// Returns the Domain\User of each group in the specified token
    /// NOTICE: This call could be very slow (30 seconds+) if many groups exist in the token, in which the names need resolving
    /// </summary>
    public static List<AccountAndAttributes> GetProcessGroupNames_AndAttributes_FromToken(IntPtr hToken)
    {
        return GetTokenGroups_FromToken(hToken).ToList();
    }


    public static int GetGroupCountForProcess(int PID)
    {
        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess == IntPtr.Zero)
        {
            return -1;
        }
        else
        {
            IntPtr hToken;
            bool opened = OpenProcessToken(hProcess, TOKEN_QUERY, out hToken);
            if (!opened || hToken == IntPtr.Zero)
            {
                return -1;
            }

            IntPtr ignored;
            TOKEN_GROUPS2 currentGroups = new TOKEN_GROUPS2();
            GetGroupsFromToken_GroupStruct(hToken, out ignored, out currentGroups);

            if (opened && hToken != IntPtr.Zero)
            {
                CloseHandle(hToken);
            }

            return (int)currentGroups.GroupCount;
        }
    }

    public static int GetGroupCountForProcess(IntPtr hProcess)
    {
        IntPtr hToken;
        bool opened = OpenProcessToken(hProcess, TOKEN_QUERY, out hToken);
        if (!opened || hToken == IntPtr.Zero)
        {
            return -1;
        }

        IntPtr ignored;
        TOKEN_GROUPS2 currentGroups = new TOKEN_GROUPS2();
        GetGroupsFromToken_GroupStruct(hToken, out ignored, out currentGroups);

        if (opened && hToken != IntPtr.Zero)
        {
            CloseHandle(hToken);
        }

        return (int)currentGroups.GroupCount;
    }

    public static int GetGroupCountFromToken(IntPtr hToken)
    {
        IntPtr ignored;
        TOKEN_GROUPS2 currentGroups = new TOKEN_GROUPS2();
        GetGroupsFromToken_GroupStruct(hToken, out ignored, out currentGroups);
        return (int)currentGroups.GroupCount;
    }

    public static bool GetGroupsFromToken_GroupStruct(IntPtr hToken, out IntPtr groupInfoBuffer, out TOKEN_GROUPS2 groups)
    {
        groupInfoBuffer = IntPtr.Zero;
        groups = new TOKEN_GROUPS2();
        bool retSuccess = false;

        // Request the size
        uint returnedSize;
        GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenGroups, IntPtr.Zero, 0, out returnedSize);

        // Allocate space, and recall the function
        // See here - goobbe.com/questions/2671574/getting-logged-on-username-from-a-service
        groupInfoBuffer = Marshal.AllocHGlobal((int)returnedSize);
        retSuccess = GetTokenInformation(hToken, TOKEN_INFORMATION_CLASS.TokenGroups, groupInfoBuffer, returnedSize, out returnedSize);
        if (retSuccess)
        {
            groups = (TOKEN_GROUPS2)Marshal.PtrToStructure(groupInfoBuffer, typeof(TOKEN_GROUPS2));

            return retSuccess;
        }

        return retSuccess;
    }

    /// <summary>
    /// NOTICE: Slow to resolve for large number of groups in a token when connected to a domain and they need to be resolved
    ///         which calls GetAccountInfoFromSidPtr > LookupAccountSid_Ptr
    ///         
    /// Opt for GetTokenGroups_FromToken_SidStringsOnlyFaster if applicable to match via known SID string instead of account group name
    /// 
    /// Based on: gist.github.com/rgl/35b5f7d3e58aa96e598d2d6999b25bbe
    /// Slightly modified
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public static AccountAndAttributes[] GetTokenGroups_FromToken(IntPtr token, bool translateSIDToName_SlowOnDomains = true)
    {
        const int bufferSize = 10 * 1024;                   // Starting size
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            int requiredSize = bufferSize;
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenGroups, buffer, bufferSize, out requiredSize))
            {
                //Status("[GetTokenGroups_FromToken] : GetTokenInformation failed: " + GetLastErrorInfo());
            }

            var tokenGroupsStructure = (TOKEN_GROUPS2)Marshal.PtrToStructure(buffer, typeof(TOKEN_GROUPS2));

            // Array in memory --> Array
            var sidArray = new SID_AND_ATTRIBUTES[tokenGroupsStructure.GroupCount];
            PtrToStructureArray("Groups", buffer, sidArray);

            var ret = sidArray.Select(
                    g => new AccountAndAttributes
                    {

                        Account = GetAccountInfoFromSidPtr(g.pSID),
                        AttributesNames = GetTokenGroupAttributes_FromFlags((uint)g.Attributes),
                        AttributesFlags = (uint)g.Attributes,
                    }
                ).ToArray();

            return ret;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string[] GetTokenGroupAttributes_FromFlags(uint attributes)
    {
        // TODO list unknown bits.
        if(attributes == 0)
        {
            return new string[] { TokenGroupAttributes.Disabled.ToString() };
        }
        else
        {
            return GetFlags((TokenGroupAttributes_NoDisabled)attributes).ToArray();          // Dont include 'Disabled' (0)
        }
    }

    /// <summary>
    /// Slow due to LookupAccountSid_Ptr if calling for many groups
    /// 
    /// Returns an Account object from an SID pointer
    /// </summary>
    /// <param name="sid"></param>
    /// <returns></returns>
    public static Account GetAccountInfoFromSidPtr(IntPtr sid, bool translateSIDToName_SlowOnDomains = true)
    {
        var nameStringBuilder = new StringBuilder(255);
        var nameStringBuilderCapacity = nameStringBuilder.Capacity;
        var domainStringBuilder = new StringBuilder(255);
        var domainStringBuilderCapacity = domainStringBuilder.Capacity;
        SID_NAME_USE nameUse;

        if (!translateSIDToName_SlowOnDomains)
        {
            // Quick return with no name info filled in
            return new Account
            {
                Sid = SIDPtrToSIDString(sid),
                Domain = "",
                Name = "",
                Type = "",
            };
        }

        if (sid == IntPtr.Zero)
        {
            //Status("[!] [GetAccountInfoFromSidPtr] was passed a Null SID -- Skipping translation to account info");

            return new Account
            {
                Sid = SIDPtrToSIDString(sid),
                Domain = "",
                Name = "N/A",
                Type = "",
            };
        }


        if (!LookupAccountSid_Ptr(
            null,
            sid,
            nameStringBuilder,
            ref nameStringBuilderCapacity,
            domainStringBuilder,
            ref domainStringBuilderCapacity,
            out nameUse))
        {
            //throw new Win32Exception();
            // NB in nano server the SID S-1-5-93-0 fails to be found for some reason... so we do not throw an exception on error.
            return new Account
            {
                Sid = SIDPtrToSIDString(sid),
                Domain = "",
                Name = "!!NOT-FOUND!!",
                Type = "Unknown",
            };
        }

        return new Account
        {
            Sid = SIDPtrToSIDString(sid),
            Domain = domainStringBuilder.ToString(),
            Name = nameStringBuilder.ToString(),
            Type = nameUse.ToString(),
        };
    }

    #endregion

    #region Integrity (token)

    // Another check could be to implement a privilege count OR checking for SeImpersonatePrivilege
    // To do: Implement this for other process PIDs
    public static IntegrityLevel GetCurrentProcessIntegrityLevel()
    {
        return GetIntegrityLevelOfProcess(GetCurrentProcess());
    }

    /// <summary>
    /// Returns the integrity level of a process
    /// Note: If this calling process is running as Medium integrity, then it will not be able to query the integrity of a High integrity process (result will be Unknown)
    /// However, a High (= Elevated) integrity calling process WILL be able to query the integrity of a System process
    /// </summary>
    /// <returns>Success of failure of query</returns>
    public static IntegrityLevel GetIntegrityLevelOfProcess(IntPtr hProcess, bool quiet = false)
    {
        IntegrityLevel integrityLevel = IntegrityLevel.Unknown;       // Default to the Unknown

        if (hProcess != IntPtr.Zero)
        {
            string pid = GetProcessId(hProcess).ToString();

            IntPtr hToken;
            if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE, out hToken))
            {
                if (!quiet)
                {
                    //Status(string.Format("GetIntegrityLevelOfProcess(PID = {0}) - OpenProcessToken() failed for TOKEN_DUPLICATE permission. ", pid), GetLastErrorInfo());
                }
            }
            else
            {
                IntPtr hTokenCopied;
                if (!DuplicateTokenEx(hToken, TOKEN_QUERY, IntPtr.Zero, (int)SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, (int)TOKEN_TYPE.TokenPrimary, out hTokenCopied))
                {
                    if (!quiet)
                    {
                        //Status(string.Format("GetIntegrityLevelOfProcess(PID = {0}) - DuplicateTokenEx failed. ", pid), GetLastErrorInfo());
                    }
                }
                else
                {
                    // GetTokenInformation will fail with the size of GetTokenInformation TOKEN_MANDATORY_LABEL (size = 8), when it expects a size of 20.
                    // So instead call GetTokenInformation requesting the size, and then proceeding 
                    // Example here - https://github.com/Gallio/mbunit-v3/blob/master/src/Gallio/Gallio/Common/Platform/ProcessSupport.cs

                    //int size = Marshal.SizeOf((new TOKEN_MANDATORY_LABEL()));
                    //IntPtr bufferPtr = Marshal.AllocHGlobal(size);                      // Allocate a buffer the size of TOKEN_MANDATORY_LABEL
                    //uint returnedSize = 0;
                    //bool success = GetTokenInformation(hTokenCopied, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, bufferPtr, size, out returnedSize);

                    int returnedSize = 0;
                    GetTokenInformation(hTokenCopied, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out returnedSize);

                    IntPtr bufferPtr = Marshal.AllocHGlobal(returnedSize);
                    bool success = GetTokenInformation(hTokenCopied, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, bufferPtr, returnedSize, out returnedSize);
                    if (success)
                    {
                        // CopyMemory IntPtr --> Structure
                        // Note: Even though the allocated size is larger than TML, it seems that the Marshal.PtrToStructure still does the job just fine
                        TOKEN_MANDATORY_LABEL labelRead = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(bufferPtr, typeof(TOKEN_MANDATORY_LABEL));                     // Ptr --> Structure

                        // Label --> Level
                        ConvertMandatoryLabelToIntegrityLevel(labelRead, out integrityLevel);
                    }
                    else
                    {
                        if (!quiet)
                        {
                            //Status(string.Format("GetIntegrityLevelOfProcess(PID = {0}) - Unable to read token via GetTokenInformation.", pid), GetLastErrorInfo());
                        }
                    }
                    CloseHandle(hTokenCopied);
                }
                CloseHandle(hToken);
            }
        }

        return integrityLevel;
    }

    /// <summary>
    /// Requires QueryInformation access right
    /// 
    /// Returns the integrity level of a process
    /// Note: If this calling process is running as Medium integrity, then it will not be able to query the integrity of a High integrity process (result will be Unknown)
    /// However, a High (= Elevated) integrity calling process WILL be able to query the integrity of a System process
    /// </summary>
    /// <returns>Success of failure of query</returns>
    public static IntegrityLevel GetIntegrityLevelOfProcess(int pid, bool quiet = false)
    {
        IntegrityLevel integrityLevel = IntegrityLevel.Unknown;       // Default to the Unknown

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, pid);
        integrityLevel = GetIntegrityLevelOfProcess(hProcess, quiet);
        CloseHandle(hProcess);
        return integrityLevel;
    }

    /// <summary>
    /// Returns the integrity level of a process
    /// Note: If this calling process is running as Medium integrity, then it will not be able to query the integrity of a High integrity process (result will be Unknown)
    /// However, a High (= Elevated) integrity calling process WILL be able to query the integrity of a System process
    /// </summary>
    /// <returns>Success of failure of query</returns>
    public static IntegrityLevel GetIntegrityLevelOfToken(IntPtr hToken)
    {
        IntegrityLevel integrityLevel = IntegrityLevel.Unknown;       // Default to the Unknown

        if (hToken == IntPtr.Zero)
        {
            //Status("GetIntegrityLevelOfToken was called with a NULL (IntPtr.Zero) token handle. Returning false.");
            return IntegrityLevel.Same;
        }

        IntPtr hTokenCopied;
        if (!DuplicateTokenEx(hToken, TOKEN_QUERY, IntPtr.Zero, (int)SECURITY_IMPERSONATION_LEVEL.SecurityIdentification, (int)TOKEN_TYPE.TokenPrimary, out hTokenCopied))
        {
            //Status(string.Format("GetIntegrityLevelOfToken(hToken = {0}) - DuplicateTokenEx failed. ", hToken), GetLastErrorInfo());
        }
        else
        {
            // GetTokenInformation will fail with the size of GetTokenInformation TOKEN_MANDATORY_LABEL (size = 8), when it expects a size of 20.
            // So instead call GetTokenInformation requesting the size, and then proceeding 
            // Example here - https://github.com/Gallio/mbunit-v3/blob/master/src/Gallio/Gallio/Common/Platform/ProcessSupport.cs

            int returnedSize = 0;
            GetTokenInformation(hTokenCopied, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out returnedSize);

            IntPtr bufferPtr = Marshal.AllocHGlobal(returnedSize);
            bool success = GetTokenInformation(hTokenCopied, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, bufferPtr, returnedSize, out returnedSize);
            if (success)
            {
                // CopyMemory IntPtr --> Structure
                // Note: Even though the allocated size is larger than TML, it seems that the Marshal.PtrToStructure still does the job just fine
                TOKEN_MANDATORY_LABEL labelRead = (TOKEN_MANDATORY_LABEL)Marshal.PtrToStructure(bufferPtr, typeof(TOKEN_MANDATORY_LABEL));                     // Ptr --> Structure

                // Label --> Level
                ConvertMandatoryLabelToIntegrityLevel(labelRead, out integrityLevel);
            }
            else
            {
                //Status(string.Format("GetIntegrityLevelOfToken(hToken = {0}) - Unable to read token via GetTokenInformation.", hToken), GetLastErrorInfo());
            }
            CloseHandle(hTokenCopied);
        }


        return integrityLevel;
    }

    /// <summary>
    /// Converts a [Mandatory] Label to an [Integrity] Level constant
    /// </summary>
    /// <returns></returns>
    static bool ConvertMandatoryLabelToIntegrityLevel(TOKEN_MANDATORY_LABEL label, out IntegrityLevel integrityLevel)
    {
        // Example here:
        // C++ - http://ideabase.googlecode.com/svn/alternate/ideabase/MyShell/security.cpp
        // C# - https://github.com/Gallio/mbunit-v3/blob/master/src/Gallio/Gallio/Common/Platform/ProcessSupport.cs
        // 
        // Uses GetSidSubAuthorityCount and GetSidSubAuthority

        bool ret = false;
        integrityLevel = IntegrityLevel.Unknown;

        IntPtr sidSubAuthorityCountPtr = GetSidSubAuthorityCount(label.Label.pSID);
        int sidSubAuthorityCount = Marshal.ReadInt32(sidSubAuthorityCountPtr);

        IntPtr sidSubAuthorityPtr = GetSidSubAuthority(label.Label.pSID, sidSubAuthorityCount - 1);
        int sidSubAuthority = Marshal.ReadInt32(sidSubAuthorityPtr);


        // Map the Sub Authority to a Well-Known SID:
        // The Sub-Authority will fall either on or between one of these RIDs
        // 
        // Example here
        // http://ideabase.googlecode.com/svn/alternate/ideabase/MyShell/security.cpp

        if (sidSubAuthority >= SECURITY_MANDATORY_UNTRUSTED_RID && sidSubAuthority < SECURITY_MANDATORY_LOW_RID)
        {
            integrityLevel = IntegrityLevel.Untrusted;
            ret = true;
        }
        else if (sidSubAuthority >= SECURITY_MANDATORY_LOW_RID && sidSubAuthority < SECURITY_MANDATORY_MEDIUM_RID)
        {
            integrityLevel = IntegrityLevel.Low;
            ret = true;
        }
        else if (sidSubAuthority >= SECURITY_MANDATORY_MEDIUM_RID && sidSubAuthority < SECURITY_MANDATORY_HIGH_RID)
        {
            integrityLevel = IntegrityLevel.Medium;
            ret = true;
        }
        else if (sidSubAuthority >= SECURITY_MANDATORY_HIGH_RID && sidSubAuthority < SECURITY_MANDATORY_SYSTEM_RID)
        {
            integrityLevel = IntegrityLevel.High;
            ret = true;
        }
        else if (sidSubAuthority >= SECURITY_MANDATORY_SYSTEM_RID && sidSubAuthority < SECURITY_MANDATORY_PROTECTED_PROCESS_RID)
        {
            integrityLevel = IntegrityLevel.System;
            ret = true;
        }
        else if (sidSubAuthority >= SECURITY_MANDATORY_PROTECTED_PROCESS_RID)
        {
            integrityLevel = IntegrityLevel.ProtectedProcess;
            ret = true;
        }
        else
        {
            // Handled by default
        }

        return ret;
    }

    #endregion

    #region Elevation (Integrity of High + BUILTIN\Administrators group)

    public static bool AmIElevated_IntegrityAndGroupCheck()
    {
        bool elevatedIL = AmIElevated_IntegrityLevel();
        bool elevatedAdminGroup = AmIElevated_BuiltinAdministratorsPresentAndEnabled();

        if (elevatedIL &&           //  >= High
            elevatedAdminGroup)     // Even if running as SYSTEM, then BUILTIN\Administrators group will be present for these processes
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a boolean for whether or not the integrity of the current process >= High
    /// Medium and lower = NOT elevated
    /// This may be more reliable than calling AmIElevated_TokenType
    /// </summary>
    /// <returns></returns>
    public static bool AmIElevated_IntegrityLevel()
    {
        IntegrityLevel level = GetIntegrityLevelOfProcess(GetCurrentProcess());

        if (level >= IntegrityLevel.High)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// A more true test for elevation is if BUILTIN\Administrators exist in the token, because you could have a High or System IL changed
    /// with No Administrators group from it be stripped away (like if the token was obtained from WtsQueryUserToken, and TokenLinkedToken didn't return an unfiltered High IL token, yet it was changed by the caller (using TCB)
    ///    The result would be a High/System IL process but with privilege removed to be that that of a Medium IL process + no Builtin\Administrators
    /// </summary>
    /// <returns></returns>
    public static bool AmIElevated_BuiltinAdministratorsPresentAndEnabled()
    {
        IntPtr hMyToken = IntPtr.Zero;
        bool isRet = false;

        bool got = OpenProcessToken(GetCurrentProcess(), TOKEN_READ, out hMyToken);
        if (got && hMyToken != IntPtr.Zero)
        {
            //AccountAndAttributes[] groups = GetTokenGroups_FromToken(hMyToken);
            //if (groups.FirstOrDefault(x => x.Account.Name.ToLower() == "BUILTIN\\Administrators") != null)        // SLOW due to LookupAccountSid_Ptr for large number of groups



            // Fix 10-25-2020 (Oct 2020)
            //      It is NOT Sufficient to check if it is just present. It will be present but disabled if the user is an Admin, but UAC is on.
            //      It cannot be enabled via PH either since its marked as Deny only.
            //
            //      Accurate check = Present AND Enabled

            //List<string> groupSidString = GetTokenGroups_FromToken_SidStringsOnlyFaster(hMyToken);
            //string adminGroupSidString = UsernameToSIDStr("BUILTIN\\Administrators");       // "S-1-5-32-544"
            //if (groupSidString.FirstOrDefault(x => x == adminGroupSidString) != null)

            const bool DoNameResolution_SLOWER = false;
            List<AccountAndAttributes> groupsAndAttributes = GetTokenGroups_FromToken(hMyToken, DoNameResolution_SLOWER).ToList();
            string adminGroupSidString = UsernameToSIDStr("BUILTIN\\Administrators");       // "S-1-5-32-544"

            AccountAndAttributes adminAcctInToken = groupsAndAttributes.FirstOrDefault(x => x.Account.Sid == adminGroupSidString);
            bool isPresent = (adminAcctInToken != null);
            if (!isPresent)
            {
                isRet = false;
            }
            else
            {
                uint allFlags = adminAcctInToken.AttributesFlags;
                bool enabled = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_ENABLED);
                bool enabledByDefault = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_ENABLED_BY_DEFAULT);
                bool denyUseOnly = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_USE_FOR_DENY_ONLY);

                if ((enabled || enabledByDefault) && !denyUseOnly)
                {
                    // Then the group is NOT disabled 
                    isRet = true;
                }
            }

            CloseHandle(hMyToken);
        }

        return isRet;
    }






    public static bool IsProcessElevated_IntegrityAndGroupCheck(int PID)
    {
        IntegrityLevel level = GetIntegrityLevelOfProcess(PID);
        bool isElevatedIL = IsProcessElevated_IntegrityLevel(PID);
        IsProcessElevated_BuiltinAdministratorsPresentAndEnabled(PID, out bool? elevatedAdminGroup, out bool successfullyReadGroups);

        if (!successfullyReadGroups)
        {
            // Assume the process IS elevated if the groups cannot be read
            return true;
        }

        if (isElevatedIL &&           //  >= High
            elevatedAdminGroup.HasValue && elevatedAdminGroup.Value == true)     // Even if running as SYSTEM, then BUILTIN\Administrators group will be present for these processes, so this is an accurate check
        {
            return true;
        }
        else
        {
            if (successfullyReadGroups && elevatedAdminGroup.HasValue && !elevatedAdminGroup.Value)
            {
                // Definitely not elevated if this group was readable its not in there
                return false;
            }

            if (level == IntegrityLevel.Unknown)
            {
                // Fail case 2 - No IL readable  -- Assume its elevated, esepcailly if it has BUILTIN\Administratros group
                return true;
            }

            // Otherwise any other integrity level
            return false;
        }
    }

    public static bool IsProcessElevated_IntegrityAndGroupCheck(IntPtr hProcess)
    {
        IntegrityLevel level = GetIntegrityLevelOfProcess(hProcess);
        bool isElevatedIL = level >= IntegrityLevel.High;
        IsProcessElevated_BuiltinAdministratorsPresentAndEnabled(hProcess, out bool? elevatedAdminGroup, out bool successfullyReadGroups);

        if(!successfullyReadGroups)
        {
            // Assume the process IS elevated if the groups cannot be read
            return true;
        }

        if (isElevatedIL &&           //  >= High
            elevatedAdminGroup.HasValue && elevatedAdminGroup.Value == true)     // Even if running as SYSTEM, then BUILTIN\Administrators group will be present for these processes, so this is an accurate check
        {
            return true;
        }
        else
        {
            if(successfullyReadGroups && elevatedAdminGroup.HasValue && !elevatedAdminGroup.Value)
            {
                // Definitely not elevated if this group was readable its not in there
                return false;
            }

            if(level == IntegrityLevel.Unknown)
            {
                // Fail case 2 - No IL readable  -- Assume its elevated, esepcailly if it has BUILTIN\Administratros group
                return true;
            }

            // Otherwise any other integrity level
            return false;
        }
    }

    /// <summary>
    /// Returns a boolean for whether or not the integrity of the current process >= High
    /// Medium and lower = NOT elevated
    /// </summary>
    public static bool IsProcessElevated_IntegrityLevel(int PID)
    {
        IntegrityLevel level = GetIntegrityLevelOfProcess(PID);

        if (level >= IntegrityLevel.High)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a boolean for whether or not the integrity of the current process >= High
    /// Medium and lower = NOT elevated
    /// </summary>
    public static bool IsProcessElevated_IntegrityLevel(IntPtr hProcess)
    {
        IntegrityLevel level = GetIntegrityLevelOfProcess(hProcess);

        if (level >= IntegrityLevel.High)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    /// <summary>
    /// Determines if a process is elevated (if it can be opened) by reading all token groups, and check to see if "BUILTIN\\Administrators" ("S-1-5-32-544") exists
    /// False is returned if the process cannot be opened
    /// </summary>
    public static void IsProcessElevated_BuiltinAdministratorsPresentAndEnabled(int PID, out bool? isElevated, out bool successfullyRead)
    {
        // Example call:
        //      IsProcessElevated_BuiltinAdministratorsPresent(15500, out bool? isIt, out bool success);

        IntPtr hToken = IntPtr.Zero;
        isElevated = null;
        successfullyRead = true;

        IntPtr hProcess = OpenProcess(ProcessAccessFlags.QueryInformation, false, PID);
        if (hProcess != IntPtr.Zero)
        {
            IsProcessElevated_BuiltinAdministratorsPresentAndEnabled(hProcess, out isElevated, out successfullyRead);
        }
        else
        {
            successfullyRead = false;
        }
    }

    /// <summary>
    /// Determines if a process is elevated (if it can be opened) by reading all token groups, and check to see if "BUILTIN\\Administrators" ("S-1-5-32-544") exists
    /// False is returned if the process cannot be opened
    /// </summary>
    public static void IsProcessElevated_BuiltinAdministratorsPresentAndEnabled(IntPtr hProcess, out bool? isElevated, out bool successfullyRead)
    {
        IntPtr hToken = IntPtr.Zero;
        isElevated = null;
        successfullyRead = true;

        bool got = OpenProcessToken(GetCurrentProcess(), TOKEN_READ, out hToken);
        if (got && hToken != IntPtr.Zero)
        {
            //AccountAndAttributes[] groups = GetTokenGroups_FromToken(hMyToken);
            //if (groups.FirstOrDefault(x => x.Account.Name.ToLower() == "BUILTIN\\Administrators") != null)            // SLOW due to LookupAccountSid_Ptr for large number of groups

            // Fix 10-25-2020
            //      It is NOT Sufficient to check if it is just present. It will be present but disabled if the user is an Admin, but UAC is on.
            //      It cannot be enabled via PH either since its marked as Deny only.
            //
            //      Accurate check = Present AND Enabled

            //List<string> groupSidString = GetTokenGroups_FromToken_SidStringsOnlyFaster(hMyToken);
            //string adminGroupSidString = UsernameToSIDStr("BUILTIN\\Administrators");       // "S-1-5-32-544"
            //if (groupSidString.FirstOrDefault(x => x == adminGroupSidString) != null)

            const bool DoNameResolution_SLOWER = false;
            List<AccountAndAttributes> groupsAndAttributes = GetTokenGroups_FromToken(hToken, DoNameResolution_SLOWER).ToList();
            string adminGroupSidString = UsernameToSIDStr("BUILTIN\\Administrators");       // "S-1-5-32-544"

            AccountAndAttributes adminAcctInToken = groupsAndAttributes.FirstOrDefault(x => x.Account.Sid == adminGroupSidString);
            bool isPresent = (adminAcctInToken != null);
            if (!isPresent)
            {
                isElevated = false;
            }
            else
            {
                uint allFlags = adminAcctInToken.AttributesFlags;
                bool enabled = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_ENABLED);
                bool enabledByDefault = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_ENABLED_BY_DEFAULT);
                bool denyUseOnly = HasFlag(allFlags, (uint)TokenGroupAttributes.SE_GROUP_USE_FOR_DENY_ONLY);

                if ((enabled || enabledByDefault) && !denyUseOnly)
                {
                    // Then the group is NOT disabled 
                    isElevated = true;
                }
            }

            CloseHandle(hToken);
        }
        else
        {
            successfullyRead = false;
        }

    }



    #endregion

    #region Account name resolution

    /// <summary>
    /// User or Group --> SID String Format
    /// Calls LookupAccountName
    /// Leverages UserOrGroup_ToSIDPtr (LookupAccountName) and SIDPtrToSIDString (ConvertSidToStringSid)
    /// </summary>
    /// <param name="domainAndUser">Can be the Domain\User OR Domain OR just User (but lookup may take 10+ seconds if this computer is joined to a domain, and may also fail)</param>
    public static string UsernameToSIDStr(string domainAndUser)
    {
        string sidStringRet = "";

        IntPtr pSID = UserOrGroup_ToSIDPtr(domainAndUser);
        if (pSID != IntPtr.Zero)
        {
            // ConvertSidToStringSid 
            sidStringRet = SIDPtrToSIDString(pSID);
        }
        Marshal.FreeHGlobal(pSID);
        return sidStringRet;
    }

    /// <summary>
    /// Calls LookupAccountName
    /// 
    /// Takes a User Name, Domain\User, or GroupName and converts it to an pSID (Pointer to an SID).
    /// When off the domain (not connected), then the username will resolve IF the user has logged on before (LSA Cached)
    /// Otherwise it will NOT for any groups that are assigned (cached) for the user, since the Domain isn't available off-network (just textual unresolvable SIDs will be shown, such as in PH)
    /// 
    /// Opposite of UserSIDPtrToAccountName
    /// Call FreeHGlobal(pSID) on the return value from this method anytime its no logner needed
    /// </summary>
    /// <param name="domainAndUser">Can be the Domain\User OR Domain OR just User (but lookup may take 10+ seconds if this computer is joined to a domain, and may also fail)</param>
    /// <returns></returns>
    public static IntPtr UserOrGroup_ToSIDPtr(string domainAndUser)
    {
        // Based on:
        //      netomatix.com/LookupAccountName.aspx
        //
        // Example calls:
        //
        //string aa = UsernameToSIDStr(@"NT AUTHORITY\SYSTEM");             // Works
        //string bb = UsernameToSIDStr("SYSTEM");                           // Works - Just User
        //string cc = UsernameToSIDStr(@"NT SERVICE\TrustedInstaller");     // Just 'TrustedInstaller' alone will NOT, and takes a while since it cannot find), but this isn't just a standard user
        //string dd = UsernameToSIDStr(@"NT SERVICE");                      // Domain


        //const int bufferSize = 1024;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        string strServer = null;       // Input
        SID_NAME_USE AccountType;      // Output
        int domainNameSizeIO = 0;      // Input and output
        int sidSize = 0;               // Input and output
        StringBuilder strDomainNameBuffer = new StringBuilder();        // Input and output

        IntPtr pSID = IntPtr.Zero;

        // First get the required buffer sizes for SID and domain name.
        // Expected value: False
        bool success = LookupAccountName(
                            strServer,
                            domainAndUser,
                            pSID,
                            ref sidSize,
                            null,
                            ref domainNameSizeIO,
                            out AccountType);
        if (!success)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_INSUFFICIENT_BUFFER)
            {
                // Allocate the buffers with actual sizes that are required for SID and domain name.
                strDomainNameBuffer = new StringBuilder(domainNameSizeIO);
                pSID = Marshal.AllocHGlobal(sidSize);

                success = LookupAccountName(
                    strServer,
                    domainAndUser,
                    pSID,
                    ref sidSize,
                    strDomainNameBuffer,
                    ref domainNameSizeIO,
                    out AccountType);


                // Note:
                //    If needed an SID as a byte array, just do this
                //
                //byte[] sidArray = new byte[sidSize];
                //Marshal.Copy(pSID, sidArray, 0, sidSize);

            }
            else
            {
                // Failed - Print error
                //Console.WriteLine(nErr);
            }
        }

        if (pSID == IntPtr.Zero)
        {
            //Status(string.Format("[!] [UserOrGroup_ToSIDPtr] returned a NULL SID for account or group: {0}. Any API that uses this pSID will fail with a INVALID MEMORY LOCATION erorr.", domainAndUser));
        }

        return pSID;
    }

    #endregion

    #region -- Process memory reading helpers --

    public static object BufferBytesToStructure(byte[] buffer, Type type)
    {
        //unsafe
        //{
        //    fixed (byte* p = buffer)
        //    {
        //        return Marshal.PtrToStructure(new IntPtr(p), type);
        //    }

        //return Marshal.PtrToStructure(new IntPtr(buffer), type);

        IntPtr unmanagedPointer = Marshal.AllocHGlobal(buffer.Length);
        Marshal.Copy(buffer, 0, unmanagedPointer, buffer.Length);
        object objToRet = Marshal.PtrToStructure(unmanagedPointer, type);
        Marshal.FreeHGlobal(unmanagedPointer);
        return objToRet;

        //}
        //}
    }

    public static Type ObjectFromProcessMemory<Type>(IntPtr hProcess, IntPtr lpBaseAddress) where Type : struct
    {
        return (Type)ObjectFromProcessMemory(hProcess, lpBaseAddress, typeof(Type));
    }

    public static object ObjectFromProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, Type type)
    {
        int size = Marshal.SizeOf(type);
        return BufferBytesToStructure(ReadProcessMemoryBytes(hProcess, lpBaseAddress, size), type);
    }

    public static byte[] ReadProcessMemoryBytes(IntPtr hProcess, IntPtr lpBaseAddress, int size)
    {
        byte[] buffer = new byte[size];
        IntPtr bytesRead;

        bool didRead = ReadProcessMemory(hProcess, lpBaseAddress, buffer, new IntPtr(size), out bytesRead);
        if (didRead)
        {
            if (bytesRead.ToInt64() < size)
            {
                //Status(string.Format("[!] [ReadProcessMemoryBytes] Read only {0} of {1} bytes", bytesRead, size));
            }

            return buffer;
        }

        return new byte[0];
    }

    /// <summary>
    /// Convert the 'pointer to an array', to an actual array
    /// Useful method for and array of SID_AND_ATTRIBUTES[]
    /// See use in GetGroupsFromToken with using Marshal.OffsetOf for example usage with TOKEN_GROUPS2 (walking the array of SID_AND_ATTRIBUTES[])
    /// </summary>
    public static void PtrToStructureArray<T>(IntPtr pStartOfArray, T[] arr)
    {
        // From:
        // gist.github.com/rgl/35b5f7d3e58aa96e598d2d6999b25bbe

        var stride = Marshal.SizeOf(typeof(T));
        var ptr = pStartOfArray.ToInt64();
        for (int i = 0; i < arr.Length; i++, ptr += stride)
        {
            arr[i] = (T)Marshal.PtrToStructure(new IntPtr(ptr), typeof(T));
        }
    }

    /// <summary>
    /// Convert the 'pointer to an array', to an actual array
    /// Useful method for and array of SID_AND_ATTRIBUTES[]
    /// This is like PtrToStructureArray, but the start of an array is calculated within this method by supplying the name of the field in the struct where the array starts
    ///    as well as the information buffer to get the position where that memory starts
    /// </summary>
    public static void PtrToStructureArray<T>(string arrayFieldName, IntPtr infoBuffer, T[] arr)
    {
        // Modified from this code originally:
        // gist.github.com/rgl/35b5f7d3e58aa96e598d2d6999b25bbe

        long startingFieldOfArray = Marshal.OffsetOf(typeof(TOKEN_GROUPS2), arrayFieldName).ToInt64();       // Ex: "Groups" in TOKEN_GROUPS, which will then be caclulated to have an offset of 4
        IntPtr pStartOfArray = new IntPtr(infoBuffer.ToInt64() + startingFieldOfArray);                      // Automatically knows to use a long value for this IntPtr

        var stride = Marshal.SizeOf(typeof(T));
        var ptr = pStartOfArray.ToInt64();
        for (int i = 0; i < arr.Length; i++, ptr += stride)
        {
            arr[i] = (T)Marshal.PtrToStructure(new IntPtr(ptr), typeof(T));
        }
    }

    #endregion

    #region This / Current process info

    public static int GetThisProcessSessionId()
    {
        int sessionIDOut = -1;
        bool did = ProcessIdToSessionId(GetCurrentProcessId(), ref sessionIDOut);
        if(!did)
        {
            _lastAPIError = Marshal.GetLastWin32Error();
        }
        return sessionIDOut;
    }

    #endregion

    #region String helper methods

    /// <summary>
    /// Splits out the Username and Domain from Domain\User.
    /// If "." or "" is supplied for the domain, then the computername is returned. If no domain is supplied, then no domain is returned (will not assume the computer name)
    /// </summary>
    /// <param name="userOrDomainAndUser">Input: Domain\User or just User</param>
    /// <param name="justUserSupplied">Output:   Was just the username passed in</param>
    /// <param name="userOnly">Output:   User</param>
    /// <param name="domainIfSupplied">Output: The domain supplied, or the translation to the computer name (Environment.MachineName) if "."\User or ""\User</param>
    public static void SplitUserAndDomain(string userOrDomainAndUser, out bool justUserSupplied, out string userOnly, out string domainIfSupplied)
    {
        //
        // Example call:
        //      string userOnly = user_OrUserAndDomain;
        //      string domainIfSupplied = "";
        //      bool justUserSupplied;
        //      SplitUserAndDomain(user_OrUserAndDomain, out justUserSupplied, out userOnly, out domainIfSupplied);
        //
        //      if (!justUserSupplied)
        //      {
        //          user_OrDomainAndUser = domainIfSupplied + "\\" + userOnly;      // Reassemble
        //      }

        userOnly = userOrDomainAndUser;
        domainIfSupplied = "";
        justUserSupplied = !userOrDomainAndUser.Contains("\\");

        if (justUserSupplied)
        {
            // userOnly will be accurate
        }
        else
        {
            // If the domain was supplied then split out the Domain \ User from the input
            string[] userSplit = userOrDomainAndUser.Split('\\');

            if (userSplit.Length == 1)
            {
                userOnly = userSplit[0].Trim();
            }
            else if (userSplit.Length >= 2)
            {
                domainIfSupplied = userSplit[0].Trim();
                userOnly = userSplit[1].Trim();

                // Allow "." as input to refer to the current computer (for local accounts)
                if (string.IsNullOrWhiteSpace(domainIfSupplied) || domainIfSupplied.Trim() == ".")
                {
                    domainIfSupplied = Environment.MachineName;
                }
            }
        }
    }

    #endregion

    #region Misc methods

    public static bool HasFlag(int flags, int flagToCheck)
    {
        return ((flags & flagToCheck) == flagToCheck);
    }

    public static bool HasFlag(uint flags, uint flagToCheck)
    {
        return ((flags & flagToCheck) == flagToCheck);
    }

    /// <summary>
    /// Example call:
    /// 
    ///     Can cast to any Enum
    ///     List<string> testFlags = GetFlags((ACCESS_MASK)flagTest).ToList();
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static IEnumerable<string> GetFlags(Enum input)
    {
        foreach (Enum value in Enum.GetValues(input.GetType()))
        {
            if (input.HasFlag(value))
            {
                yield return value.ToString();
            }
        }
    }

    static bool NT_SUCCESS(uint status)
    {
        return (status & 0x80000000) == 0;
    }

    #endregion
}

#endregion


#region Extension Methods

public static class ExtensionMethods
{
    // From: stackoverflow.com/a/22160480/5555423
    public static IEnumerable<TA> Except<TA, TB, TK>(
        this IEnumerable<TA> a,
        IEnumerable<TB> b,
        Func<TA, TK> selectKeyA,
        Func<TB, TK> selectKeyB,
        IEqualityComparer<TK> comparer = null)
    {
        return a.Where(aItem => !b.Select(bItem => selectKeyB(bItem)).Contains(selectKeyA(aItem), comparer));
    }
}

#endregion