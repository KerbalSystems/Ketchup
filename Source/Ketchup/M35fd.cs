﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ketchup.Api;
using UnityEngine;

namespace Ketchup
{
    public sealed class M35Fd : PartModule, IDevice
    {
        #region Constants

        private const int WordsPerSector = 512;
        private const int SectorsPerTrack = 18;
        private const int TracksPerDisk = 80;

        private const float WordsPerSecond = 30700f;
        private const float TracksPerSecond = 0.0024f;

        private const int SectorsPerDisk = SectorsPerTrack * TracksPerDisk;
        private const int WordsPerDisk = WordsPerSector * SectorsPerDisk;

        private const float SecondsPerSector = WordsPerSector / WordsPerSecond;

        #endregion

        #region Static Fields

        private static GUIStyle _styleButtonPressed;
        private static bool _isStyleInit;

        #endregion

        #region Instance Fields

        private IDcpu16 _dcpu16;
        private FloppyDisk _disk;

        private List<FloppyDisk> _allDisks = new List<FloppyDisk>();

        private ushort _interruptMessage;

        private StateCode _currentStateCode;
        private ErrorCode _lastErrorCode;

        private int _currentTrack;

        private Rect _windowPosition;
        private bool _isWindowPositionInit;
        private GuiMode _guiMode;
        private readonly Dictionary<FloppyDisk, string> _disksBeingLabeled = new Dictionary<FloppyDisk, string>();

        #endregion

        #region Device Identifiers

        public string FriendlyName
        {
            get { return @"Mackapar 3.5"" Floppy Drive (M35FD)"; }
        }

        public uint ManufacturerId
        {
            get { return (uint)Constants.ManufacturerId.Mackapar; }
        }

        public uint DeviceId
        {
            get { return (uint)Constants.DeviceId.M35FdFloppyDrive; }
        }

        public ushort Version
        {
            get { return 0x000b; }
        }

        #endregion

        #region IDevice Methods

        public void OnConnect(IDcpu16 dcpu16)
        {
            _dcpu16 = dcpu16;
        }

        public void OnDisconnect()
        {
            _dcpu16 = null;
        }

        public int OnInterrupt()
        {
            if (_dcpu16 != null)
            {
                switch ((InterruptOperation)_dcpu16.A)
                {
                    case InterruptOperation.PollDevice:
                        HandlePollDevice();
                        break;

                    case InterruptOperation.SetInterrupt:
                        HandleSetInterrupt(_dcpu16.X);
                        break;

                    case InterruptOperation.ReadSector:
                        HandleReadSector(_dcpu16.X, _dcpu16.Y);
                        break;

                    case InterruptOperation.WriteSector:
                        HandleWriteSector(_dcpu16.X, _dcpu16.Y);
                        break;
                }
            }

            return 0;
        }

        #endregion

        #region PartModule Methods

        public override void OnStart(StartState state)
        {
            if (state != StartState.Editor)
            {
                InitStylesIfNecessary();
                InitWindowPositionIfNecessary();
                RenderingManager.AddToPostDrawQueue(1, OnDraw);
            }
        }

        #endregion

        #region Helper Methods

        private void EjectDisk()
        {
            _disk = null;

            SetErrorOrState(state: StateCode.NoMedia);
        }

        private void InsertDisk(FloppyDisk disk)
        {
            _disk = disk;

            SetErrorOrState(state: disk.IsWriteProtected ? StateCode.ReadyWp : StateCode.Ready);
        }

        private void SetErrorOrState(ErrorCode? error = null, StateCode? state = null)
        {
            var doInterrupt = _dcpu16 != null && _interruptMessage != 0 && (error != _lastErrorCode || state != _currentStateCode);

            if (error != null)
            {
                _lastErrorCode = error.Value;
            }

            if (state != null)
            {
                _currentStateCode = state.Value;
            }

            if (doInterrupt)
            {
                _dcpu16.Interrupt(_interruptMessage);
            }
        }

        private void HandlePollDevice()
        {
            _dcpu16.B = (ushort)_currentStateCode;
            _dcpu16.C = (ushort)_lastErrorCode;

            SetErrorOrState(error: ErrorCode.None);
        }

        private void HandleSetInterrupt(ushort interruptMessage)
        {
            _interruptMessage = interruptMessage;
        }

        private void HandleReadSector(ushort sector, ushort address)
        {
            switch (_currentStateCode)
            {
                case StateCode.Ready:
                case StateCode.ReadyWp:
                    _dcpu16.B = 1;

                    StartCoroutine(TransferCoroutine(sector, address, TransferOpreration.Read));

                    break;

                case StateCode.NoMedia:
                    _dcpu16.B = 0;
                    SetErrorOrState(error: ErrorCode.NoMedia);
                    break;

                case StateCode.Busy:
                    _dcpu16.B = 0;
                    SetErrorOrState(error: ErrorCode.Busy);
                    break;
            }
        }

        private void HandleWriteSector(ushort sector, ushort address)
        {
            switch (_currentStateCode)
            {
                case StateCode.Ready:
                    _dcpu16.B = 1;
                    StartCoroutine(TransferCoroutine(sector, address, TransferOpreration.Write));
                    break;

                case StateCode.ReadyWp:
                    _lastErrorCode = ErrorCode.Protected;
                    _dcpu16.B = 0;
                    break;

                case StateCode.NoMedia:
                    _lastErrorCode = ErrorCode.NoMedia;
                    _dcpu16.B = 0;
                    break;

                case StateCode.Busy:
                    _lastErrorCode = ErrorCode.Busy;
                    _dcpu16.B = 0;
                    break;
            }
        }

        private IEnumerator TransferCoroutine(ushort sector, ushort address, TransferOpreration operation)
        {
            SetErrorOrState(state: StateCode.Busy);

            var targetTrack = TrackForSector(sector);
            var trackDifference = Math.Abs(targetTrack - _currentTrack);

            if (trackDifference != 0)
            {
                yield return new WaitForSeconds(trackDifference * TracksPerSecond);
            }

            if (TransferOkToContinue(sector))
            {
                _currentTrack = targetTrack;

                yield return new WaitForSeconds(SecondsPerSector);

                if (TransferOkToContinue(sector))
                {
                    ushort[] source, destination;
                    int sourceIndex, destinationIndex;

                    switch (operation)
                    {
                        case TransferOpreration.Read:
                            source = _disk.GetSector(sector);
                            sourceIndex = 0;
                            destination = _dcpu16.Memory;
                            destinationIndex = address;
                            break;
                        case TransferOpreration.Write:
                            source = _dcpu16.Memory;
                            sourceIndex = address;
                            destination = _disk.GetSector(sector);
                            destinationIndex = 0;
                            break;
                        default:
                            throw new Exception(String.Format("Unexpected operation: {0}.", operation));
                    }

                    Array.Copy(source, sourceIndex, destination, destinationIndex, WordsPerSector);
                }
            }

            if (_currentStateCode == StateCode.Busy)
            {
                SetErrorOrState(state: _disk.IsWriteProtected ? StateCode.ReadyWp : StateCode.Ready);
            }

            yield return null;
        }

        private bool TransferOkToContinue(ushort sector)
        {
            if (_currentStateCode == StateCode.NoMedia)
            {
                SetErrorOrState(error: ErrorCode.Eject);
                return false;
            }

            if (_disk.IsBadSector(sector))
            {
                SetErrorOrState(error: ErrorCode.BadSector);
                return false;
            }

            return true;
        }

        private void OnDraw()
        {
            if (vessel.isActiveVessel)
            {
                GUI.skin = HighLogic.Skin;

                _windowPosition = GUILayout.Window(4, _windowPosition, OnM35FdWindow, "M35FD");
            }
        }

        private void OnM35FdWindow(int windowId)
        {
            GUI.skin = HighLogic.Skin;

            var insertEjectButtonPressed = false;
            var cancelInsertButtonPressed = false;

            GUILayout.BeginHorizontal();
            GUILayout.Label(_currentStateCode == StateCode.NoMedia ? "<Empty>" : _disk.Label);

            switch(_guiMode)
            {
                case GuiMode.Normal:
                    if (_allDisks.Any())
                    {
                        insertEjectButtonPressed = GUILayout.Button(_currentStateCode == StateCode.NoMedia ? "Insert" : "Eject");
                    }
                    break;

                case GuiMode.Insert:
                    cancelInsertButtonPressed = GUILayout.Button("Insert", _styleButtonPressed);
                    break;
            }
            
            GUILayout.EndHorizontal();

            var disksToDestroy = new List<FloppyDisk>();

            var availableDisks = _allDisks.Where(i => i != _disk).ToList();

            if (availableDisks.Any())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Disks:");
                GUILayout.EndHorizontal();

                foreach (var disk in availableDisks)
                {
                    GUILayout.BeginHorizontal();

                    switch (_guiMode)
                    {
                        case GuiMode.Normal:
                        case GuiMode.Get:
                            if (_disksBeingLabeled.ContainsKey(disk))
                            {
                                _disksBeingLabeled[disk] = GUILayout.TextField(_disksBeingLabeled[disk], GUILayout.Width(125));
                            }
                            else
                            {
                                GUILayout.Label(disk.Label, GUILayout.Width(125));
                            }

                            if (GUILayout.Button("Label"))
                            {
                                if (_disksBeingLabeled.ContainsKey(disk))
                                {
                                    var label = _disksBeingLabeled[disk];

                                    if (!String.IsNullOrEmpty(label) && !String.IsNullOrEmpty(label.Trim()))
                                    {
                                        disk.Label = label;
                                    }

                                    _disksBeingLabeled.Remove(disk);
                                }
                                else
                                {
                                    _disksBeingLabeled.Add(disk, disk.Label);
                                }
                            }

                            if (disk.IsWriteProtected)
                            {
                                if (GUILayout.Button("Protect", _styleButtonPressed))
                                {
                                    disk.IsWriteProtected = !disk.IsWriteProtected;
                                }
                            }
                            else
                            {
                                if (GUILayout.Button("Protect"))
                                {
                                    disk.IsWriteProtected = !disk.IsWriteProtected;
                                }
                            }

                            if (GUILayout.Button("Destroy"))
                            {
                                disksToDestroy.Add(disk);
                            }

                            break;

                        case GuiMode.Insert:
                            if (GUILayout.Button(disk.Label))
                            {
                                _guiMode = GuiMode.Normal;
                                InsertDisk(disk);
                            }

                            break;
                    }
                    GUILayout.EndHorizontal();
                }
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("No Available Disks");
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            var getDiskButtonPressed = GUILayout.Button("Get Disk");
            GUILayout.EndHorizontal();

            if (_guiMode == GuiMode.Get)
            {
                var disks = GetDiskImages().ToList();

                GUILayout.Label("Disk Images:");

                foreach (var disk in disks)
                {
                    if (GUILayout.Button(disk.Label))
                    {
                        _allDisks.Add(disk);
                        _allDisks = _allDisks.OrderBy(i => i.Label).ToList();

                        _guiMode = GuiMode.Normal;
                    }
                }
            }

            GUI.DragWindow();

            if (GUI.changed)
            {
                if (insertEjectButtonPressed)
                {
                    if (_currentStateCode == StateCode.NoMedia)
                    {
                        _guiMode = GuiMode.Insert;
                    }
                    else
                    {
                        EjectDisk();
                    }
                }

                if (getDiskButtonPressed)
                {
                    _guiMode = GuiMode.Get;
                }

                if (cancelInsertButtonPressed)
                {
                    _guiMode = GuiMode.Normal;
                }

                _windowPosition = new Rect(_windowPosition) { width = 300, height = 0 };
            }

            foreach (var disk in disksToDestroy)
            {
                _allDisks.Remove(disk);
            }
        }

        private void InitWindowPositionIfNecessary()
        {
            if (!_isWindowPositionInit)
            {
                const float defaultTop = 200f;

                _windowPosition = new Rect(Screen.width - 550f, defaultTop, 300f, 0);

                _isWindowPositionInit = true;
            }
        }

        private static IEnumerable<FloppyDisk> GetDiskImages()
        {
            yield return new FloppyDisk("<Blank Disk>", new ushort[0]);

            var savesDirectory = Path.Combine(KSPUtil.ApplicationRootPath, "saves");
            var profileDirectory = Path.Combine(savesDirectory, HighLogic.SaveFolder);
            var ketchupDirectory = Path.Combine(profileDirectory, "Ketchup");
            var diskImageDirectory = Path.Combine(ketchupDirectory, "FloppyDisks");

            if (Directory.Exists(diskImageDirectory))
            {
                foreach (var file in Directory.GetFiles(diskImageDirectory))
                {
                    var fileLower = file.ToLowerInvariant();

                    if (fileLower.EndsWith(".bin") || fileLower.EndsWith(".img"))
                    {
                        var fileInfo = new FileInfo(file);

                        if (fileInfo.Length / 2 <= WordsPerDisk)
                        {

                            var diksImageBytes = File.ReadAllBytes(file);
                            var diskImageUShorts = new ushort[diksImageBytes.Length / 2];

                            for (var i = 0; i < diksImageBytes.Length; i += 2)
                            {
                                var a = diksImageBytes[i];
                                var b = diksImageBytes[i + 1];

                                diskImageUShorts[i / 2] = (ushort)((a << 8) | b);
                            }

                            yield return new FloppyDisk(fileInfo.Name.Substring(0, fileInfo.Name.Length - 4), diskImageUShorts);
                        }
                    }
                }
            }
        }

        private static void InitStylesIfNecessary()
        {
            if (!_isStyleInit)
            {
                _styleButtonPressed = new GUIStyle(HighLogic.Skin.button) { normal = HighLogic.Skin.button.active };

                _isStyleInit = true;
            }
        }

        private static int TrackForSector(ushort sector)
        {
            return sector / SectorsPerTrack;
        }

        private static bool IsValidSector(ushort sector)
        {
            return sector < SectorsPerDisk;
        }

        #endregion

        #region Nested Types

        private enum InterruptOperation : ushort
        {
            PollDevice      = 0x0000,
            SetInterrupt    = 0x0001,
            ReadSector      = 0x0002,
            WriteSector     = 0x0003,
        }

        private enum ErrorCode : ushort
        {
            None        = 0x0000,
            Busy        = 0x0001,
            NoMedia     = 0x0002,
            Protected   = 0x0003,
            Eject       = 0x0004,
            BadSector   = 0x0005,
            // ReSharper disable once UnusedMember.Local
            Broken      = 0xffff,
        }

        private enum StateCode : ushort
        {
            NoMedia = 0x0000,
            Ready   = 0x0001,
            ReadyWp = 0x0002,
            Busy    = 0x0003,
        }

        private enum TransferOpreration : byte
        {
            Read    = 1,
            Write   = 2,
        }

        private enum GuiMode : byte
        {
            Normal  = 0,
            Insert  = 1,
            Get     = 2,
        }

        private sealed class FloppyDisk
        {
            #region Instance Fields

            private readonly Dictionary<ushort, ushort[]> _sectors;

            private bool _isWriteProtected;
            private string _label;

            #endregion

            #region Properties

            // ReSharper disable once ConvertToAutoProperty
            public bool IsWriteProtected
            {
                get { return _isWriteProtected; }
                set { _isWriteProtected = value; }
            }

            // ReSharper disable once ConvertToAutoProperty
            public string Label
            {
                get { return _label; }
                set { _label = value; }
            }

            #endregion

            #region Constructors

            public FloppyDisk(string label, ushort[] diskImage)
            {
                _label = label;
                _sectors = ConvertDiskImage(diskImage);
            }

            #endregion

            #region Methods

            public bool IsBadSector(ushort sector)
            {
                if (!IsValidSector(sector))
                {
                    throw new Exception(String.Format("Bad sector number: {0}.", sector));
                }

                return false;
            }

            public ushort[] GetSector(ushort sector)
            {
                if (!IsValidSector(sector))
                {
                    throw new Exception(String.Format("Bad sector number: {0}.", sector));
                }

                if (!_sectors.ContainsKey(sector))
                {
                    _sectors[sector] = new ushort[WordsPerSector];
                }

                return _sectors[sector];
            }

            #endregion

            #region Helpers Methods

            private static Dictionary<ushort, ushort[]> ConvertDiskImage(ushort[] diskImage)
            {
                if (diskImage.Length > WordsPerDisk)
                {
                    throw new Exception("Disk too large."); // TODO: better exception
                }

                var sectors = new Dictionary<ushort, ushort[]>();

                var numberOfSectors = (diskImage.Length / WordsPerSector) + 1;
                for (ushort i = 0; i < numberOfSectors; i++)
                {
                    var sector = new ushort[WordsPerSector];

                    var start = i * WordsPerSector;
                    var end = Math.Min(start + WordsPerSector, diskImage.Length);

                    Array.Copy(diskImage, start, sector, 0, end - start);

                    sectors[i] = sector;
                }

                return sectors;
            }

            #endregion
        }

        #endregion
    }
}
