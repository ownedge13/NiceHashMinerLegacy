﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using NiceHashMiner.Configs;
using NiceHashMiner.Devices;
using NiceHashMiner.Enums;
using NiceHashMiner.Miners;
using NiceHashMiner.Interfaces;

namespace NiceHashMiner.Forms {
    using NiceHashMiner.Miners.Grouping;
    using NiceHashMiner.Net20_backport;
    public partial class Form_Benchmark : Form, IListItemCheckColorSetter, IBenchmarkComunicator, IBenchmarkCalculation {

        private bool _inBenchmark = false;
        private int _bechmarkCurrentIndex = 0;
        private int _bechmarkedSuccessCount = 0;
        private int _benchmarkAlgorithmsCount = 0;
        private AlgorithmBenchmarkSettingsType _algorithmOption = AlgorithmBenchmarkSettingsType.SelectedUnbenchmarkedAlgorithms;

        private List<Miner> _benchmarkMiners;
        private Miner _currentMiner;
        private List<Tuple<ComputeDevice, Queue<Algorithm>>> _benchmarkDevicesAlgorithmQueue;

        private bool ExitWhenFinished = false;
        private AlgorithmType _singleBenchmarkType = AlgorithmType.NONE;

        private Timer _benchmarkingTimer;
        private int dotCount = 0;

        public bool StartMining { get; private set; }

        private struct DeviceAlgo {
            public string Device { get; set; }
            public string Algorithm { get; set; }
        }
        private List<DeviceAlgo> _benchmarkFailedAlgoPerDev;

        private enum BenchmarkSettingsStatus : int {
            NONE = 0,
            TODO,
            DISABLED_NONE,
            DISABLED_TODO
        }
        private Dictionary<string, BenchmarkSettingsStatus> _benchmarkDevicesAlgorithmStatus;
        private ComputeDevice _currentDevice;
        private Algorithm _currentAlgorithm;

        private string CurrentAlgoName;

        // CPU benchmarking helpers
        private class CPUBenchmarkStatus {
            public bool HasAlreadyBenchmarked = false; // after first benchmark set to true
            public double BenchmarkSpeed;
            public int LessTreads = 0;
            public int Time;
        }
        private CPUBenchmarkStatus __CPUBenchmarkStatus = null;

        // CPU sweet spots
        private List<AlgorithmType> CPUAlgos = new List<AlgorithmType>() {
            AlgorithmType.CryptoNight
        };

        private static Color DISABLED_COLOR = Color.DarkGray;
        private static Color BENCHMARKED_COLOR = Color.LightGreen;
        private static Color UNBENCHMARKED_COLOR = Color.LightBlue;
        public void LviSetColor(ListViewItem lvi) {
            var CDevice = lvi.Tag as ComputeDevice;
            if (CDevice != null && _benchmarkDevicesAlgorithmStatus != null) {
                var uuid = CDevice.UUID;
                if (!CDevice.Enabled) {
                    lvi.BackColor = DISABLED_COLOR;
                } else {
                    switch (_benchmarkDevicesAlgorithmStatus[uuid]) {
                        case BenchmarkSettingsStatus.TODO:
                        case BenchmarkSettingsStatus.DISABLED_TODO:
                            lvi.BackColor = UNBENCHMARKED_COLOR;
                            break;
                        case BenchmarkSettingsStatus.NONE:
                        case BenchmarkSettingsStatus.DISABLED_NONE:
                            lvi.BackColor = BENCHMARKED_COLOR;
                            break;
                    }
                }
                //// enable disable status, NOT needed
                //if (cdvo.IsEnabled && _benchmarkDevicesAlgorithmStatus[uuid] >= BenchmarkSettingsStatus.DISABLED_NONE) {
                //    _benchmarkDevicesAlgorithmStatus[uuid] -= 2;
                //} else if (!cdvo.IsEnabled && _benchmarkDevicesAlgorithmStatus[uuid] <= BenchmarkSettingsStatus.TODO) {
                //    _benchmarkDevicesAlgorithmStatus[uuid] += 2;
                //}
            }
        }

        public Form_Benchmark(BenchmarkPerformanceType benchmarkPerformanceType = BenchmarkPerformanceType.Standard,
            bool autostart = false,
            //List<ComputeDevice> enabledDevices = null,
            AlgorithmType singleBenchmarkType = AlgorithmType.NONE) {
            
            InitializeComponent();
            this.Icon = NiceHashMiner.Properties.Resources.logo;

            StartMining = false;
            _singleBenchmarkType = singleBenchmarkType;

            benchmarkOptions1.SetPerformanceType(benchmarkPerformanceType);
            
            // benchmark only unique devices
            devicesListViewEnableControl1.SetIListItemCheckColorSetter(this);
            devicesListViewEnableControl1.SetComputeDevices(ComputeDeviceManager.Avaliable.AllAvaliableDevices);

            // use this to track miner benchmark statuses
            _benchmarkMiners = new List<Miner>();

            InitLocale();

            _benchmarkingTimer = new Timer();
            _benchmarkingTimer.Tick += BenchmarkingTimer_Tick;
            _benchmarkingTimer.Interval = 1000; // 1s

            // name, UUID
            Dictionary<string, string> benchNamesUUIDs = new Dictionary<string, string>();
            // initialize benchmark settings for same cards to only copy settings
            foreach (var cDev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                var plainDevName = cDev.Name;
                if (benchNamesUUIDs.ContainsKey(plainDevName)) {
                    cDev.Enabled = false;
                    cDev.BenchmarkCopyUUID = benchNamesUUIDs[plainDevName];
                } else {
                    benchNamesUUIDs.Add(plainDevName, cDev.UUID);
                    cDev.Enabled = true; // enable benchmark
                    cDev.BenchmarkCopyUUID = null;
                }
            }

            //groupBoxAlgorithmBenchmarkSettings.Enabled = _singleBenchmarkType == AlgorithmType.NONE;
            devicesListViewEnableControl1.Enabled = _singleBenchmarkType == AlgorithmType.NONE;
            devicesListViewEnableControl1.SetDeviceSelectionChangedCallback(devicesListView1_ItemSelectionChanged);

            devicesListViewEnableControl1.SetAlgorithmsListView(algorithmsListView1);
            devicesListViewEnableControl1.IsBenchmarkForm = true;
            devicesListViewEnableControl1.IsSettingsCopyEnabled = true;

            ResetBenchmarkProgressStatus();
            CalcBenchmarkDevicesAlgorithmQueue();
            devicesListViewEnableControl1.ResetListItemColors();

            // to update laclulation status
            devicesListViewEnableControl1.BenchmarkCalculation = this;
            algorithmsListView1.BenchmarkCalculation = this;


            // set first device selected {
            if (ComputeDeviceManager.Avaliable.AllAvaliableDevices.Count > 0) {
                var firstComputedevice = ComputeDeviceManager.Avaliable.AllAvaliableDevices[0];
                algorithmsListView1.SetAlgorithms(firstComputedevice, firstComputedevice.Enabled);
            }

            if (autostart) {
                ExitWhenFinished = true;
                StartStopBtn_Click(null, null);
            }
        }

        private void CopyBenchmarks() {
            Helpers.ConsolePrint("CopyBenchmarks", "Checking for benchmarks to copy");
            foreach (var cDev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                // check if copy
                if (!cDev.Enabled && cDev.BenchmarkCopyUUID != null) {
                    var copyCdevSettings = ComputeDeviceManager.Avaliable.GetDeviceWithUUID(cDev.BenchmarkCopyUUID);
                    if (copyCdevSettings != null) {
                        Helpers.ConsolePrint("CopyBenchmarks", String.Format("Copy from {0} to {1}", cDev.UUID, cDev.BenchmarkCopyUUID));
                        cDev.CopyBenchmarkSettingsFrom(copyCdevSettings);
                    }
                } 
            }
        }

        private void BenchmarkingTimer_Tick(object sender, EventArgs e) {
            if (_inBenchmark) {
                algorithmsListView1.SetSpeedStatus(_currentDevice, _currentAlgorithm.NiceHashID, getDotsWaitString());
            }
        }

        private string getDotsWaitString() {
            ++dotCount;
            if (dotCount > 3) dotCount = 1;
            return new String('.', dotCount);
        }

        private void InitLocale() {
            this.Text = International.GetText("Form_Benchmark_title"); //International.GetText("SubmitResultDialog_title");
            //labelInstruction.Text = International.GetText("SubmitResultDialog_labelInstruction");
            StartStopBtn.Text = International.GetText("SubmitResultDialog_StartBtn");
            CloseBtn.Text = International.GetText("SubmitResultDialog_CloseBtn");

            // TODO fix locale for benchmark enabled label
            devicesListViewEnableControl1.InitLocale();
            benchmarkOptions1.InitLocale();
            algorithmsListView1.InitLocale();
            groupBoxBenchmarkProgress.Text = International.GetText("FormBenchmark_Benchmark_GroupBoxStatus");
            radioButton_SelectedUnbenchmarked.Text = International.GetText("FormBenchmark_Benchmark_All_Selected_Unbenchmarked");
            radioButton_RE_SelectedUnbenchmarked.Text = International.GetText("FormBenchmark_Benchmark_All_Selected_ReUnbenchmarked");
            checkBox_StartMiningAfterBenchmark.Text = International.GetText("Form_Benchmark_checkbox_StartMiningAfterBenchmark");
        }

        private void StartStopBtn_Click(object sender, EventArgs e) {
            if (_inBenchmark) {
                StopButonClick();
                BenchmarkStoppedGUISettings();
            } else if (StartButonClick()) {
                StartStopBtn.Text = International.GetText("Form_Benchmark_buttonStopBenchmark");
            }
        }

        private void BenchmarkStoppedGUISettings() {
            StartStopBtn.Text = International.GetText("Form_Benchmark_buttonStartBenchmark");
            // clear benchmark pending status
            if (_currentAlgorithm != null) _currentAlgorithm.ClearBenchmarkPending();
            foreach (var deviceAlgosTuple in _benchmarkDevicesAlgorithmQueue) {
                foreach (var algo in deviceAlgosTuple.Item2) {
                    algo.ClearBenchmarkPending();
                }
            }
            ResetBenchmarkProgressStatus();
            CalcBenchmarkDevicesAlgorithmQueue();
            benchmarkOptions1.Enabled = true;

            algorithmsListView1.IsInBenchmark = false;
            devicesListViewEnableControl1.IsInBenchmark = false;
            if (_currentDevice != null) {
                algorithmsListView1.RepaintStatus(_currentDevice.Enabled, _currentDevice.UUID);
            }

            CloseBtn.Enabled = true;
        }

        // TODO add list for safety and kill all miners
        private void StopButonClick() {
            _benchmarkingTimer.Stop();
            _inBenchmark = false;
            Helpers.ConsolePrint("FormBenchmark", "StopButonClick() benchmark routine stopped");
            // copy benchmarked
            CopyBenchmarks();
            if (_currentMiner != null) {
                _currentMiner.BenchmarkSignalQuit = true;
                _currentMiner.InvokeBenchmarkSignalQuit();
            }
            if (ExitWhenFinished) {
                this.Close();
            }
        }

        private bool StartButonClick() {
            CalcBenchmarkDevicesAlgorithmQueue();
            // device selection check scope
            {
                bool noneSelected = true;
                foreach (var cDev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                    if (cDev.Enabled) {
                        noneSelected = false;
                        break;
                    }
                }
                if (noneSelected) {
                    MessageBox.Show(International.GetText("FormBenchmark_No_Devices_Selected_Msg"),
                        International.GetText("FormBenchmark_No_Devices_Selected_Title"),
                        MessageBoxButtons.OK);
                    return false;
                }
            }
            // device todo benchmark check scope
            {
                bool nothingToBench = true;
                foreach (var statusKpv in _benchmarkDevicesAlgorithmStatus) {
                    if (statusKpv.Value == BenchmarkSettingsStatus.TODO) {
                        nothingToBench = false;
                        break;
                    }
                }
                if (nothingToBench) {
                    MessageBox.Show(International.GetText("FormBenchmark_Nothing_to_Benchmark_Msg"),
                        International.GetText("FormBenchmark_Nothing_to_Benchmark_Title"),
                        MessageBoxButtons.OK);
                    return false;
                }
            }

            // current failed new list
            _benchmarkFailedAlgoPerDev = new List<DeviceAlgo>();
            // disable gui controls
            benchmarkOptions1.Enabled = false;
            CloseBtn.Enabled = false;
            algorithmsListView1.IsInBenchmark = true;
            devicesListViewEnableControl1.IsInBenchmark = true;
            // set benchmark pending status
            foreach (var deviceAlgosTuple in _benchmarkDevicesAlgorithmQueue) {
                foreach (var algo in deviceAlgosTuple.Item2) {
                    algo.SetBenchmarkPending();
                }
            }
            if (_currentDevice != null) {
                algorithmsListView1.RepaintStatus(_currentDevice.Enabled, _currentDevice.UUID);
            }

            StartBenchmark();

            return true;
        }

        public void CalcBenchmarkDevicesAlgorithmQueue() {

            _benchmarkAlgorithmsCount = 0;
            _benchmarkDevicesAlgorithmStatus = new Dictionary<string, BenchmarkSettingsStatus>();
            _benchmarkDevicesAlgorithmQueue = new List<Tuple<ComputeDevice, Queue<Algorithm>>>();
            foreach (var cDev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                var algorithmQueue = new Queue<Algorithm>();
                if (_singleBenchmarkType == AlgorithmType.NONE) {
                    foreach (var kvpAlgorithm in cDev.AlgorithmSettings) {
                        if (ShoulBenchmark(kvpAlgorithm.Value)) {
                            algorithmQueue.Enqueue(kvpAlgorithm.Value);
                            kvpAlgorithm.Value.SetBenchmarkPendingNoMsg();
                        } else {
                            kvpAlgorithm.Value.ClearBenchmarkPending();
                        }
                    }
                } else { // single bench
                    var algo = cDev.AlgorithmSettings[_singleBenchmarkType];
                    algorithmQueue.Enqueue(algo);
                }
                

                BenchmarkSettingsStatus status;
                if (cDev.Enabled) {
                    _benchmarkAlgorithmsCount += algorithmQueue.Count;
                    status = algorithmQueue.Count == 0 ? BenchmarkSettingsStatus.NONE : BenchmarkSettingsStatus.TODO;
                    _benchmarkDevicesAlgorithmQueue.Add(
                    new Tuple<ComputeDevice, Queue<Algorithm>>(cDev, algorithmQueue)
                    );
                } else {
                    status = algorithmQueue.Count == 0 ? BenchmarkSettingsStatus.DISABLED_NONE : BenchmarkSettingsStatus.DISABLED_TODO;
                }
                _benchmarkDevicesAlgorithmStatus[cDev.UUID] = status;
            }
            // GUI stuff
            progressBarBenchmarkSteps.Maximum = _benchmarkAlgorithmsCount;
            progressBarBenchmarkSteps.Value = 0;
            SetLabelBenchmarkSteps(0, _benchmarkAlgorithmsCount);
        }

        private bool ShoulBenchmark(Algorithm algorithm) {
            bool isBenchmarked = algorithm.BenchmarkSpeed > 0 ? true : false;
            if (_algorithmOption == AlgorithmBenchmarkSettingsType.SelectedUnbenchmarkedAlgorithms
                && !isBenchmarked && !algorithm.Skip) {
                    return true;
            }
            if (_algorithmOption == AlgorithmBenchmarkSettingsType.UnbenchmarkedAlgorithms && !isBenchmarked) {
                return true;
            }
            if (_algorithmOption == AlgorithmBenchmarkSettingsType.ReBecnhSelectedAlgorithms && !algorithm.Skip) {
                return true;
            }
            if (_algorithmOption == AlgorithmBenchmarkSettingsType.AllAlgorithms) {
                return true;
            }

            return false;
        }

        void StartBenchmark() {
            _inBenchmark = true;
            _bechmarkCurrentIndex = -1;
            NextBenchmark();
        }

        void NextBenchmark() {
            if (_bechmarkCurrentIndex > -1) {
                StepUpBenchmarkStepProgress();
            } 
            ++_bechmarkCurrentIndex;
            if (_bechmarkCurrentIndex >= _benchmarkAlgorithmsCount) {
                EndBenchmark();
                return;
            }

            Tuple<ComputeDevice, Queue<Algorithm>> currentDeviceAlgosTuple;
            Queue<Algorithm> algorithmBenchmarkQueue;
            while (_benchmarkDevicesAlgorithmQueue.Count > 0) {
                currentDeviceAlgosTuple = _benchmarkDevicesAlgorithmQueue[0];
                _currentDevice = currentDeviceAlgosTuple.Item1;
                algorithmBenchmarkQueue = currentDeviceAlgosTuple.Item2;
                if(algorithmBenchmarkQueue.Count != 0) {
                    _currentAlgorithm = algorithmBenchmarkQueue.Dequeue();
                    break;
                } else {
                    _benchmarkDevicesAlgorithmQueue.RemoveAt(0);
                }
            }

            if (_currentDevice != null && _currentAlgorithm != null) {
                _currentMiner = MinersManager.CreateMiner(_currentDevice, _currentAlgorithm);
                if(_currentDevice.DeviceType == DeviceType.CPU && string.IsNullOrEmpty(_currentAlgorithm.ExtraLaunchParameters)) {
                    __CPUBenchmarkStatus = new CPUBenchmarkStatus();
                    _currentAlgorithm.LessThreads = __CPUBenchmarkStatus.LessTreads;
                } else {
                    __CPUBenchmarkStatus = null;
                }
            }

            if (_currentMiner != null && _currentAlgorithm != null) {
                _benchmarkMiners.Add(_currentMiner);
                CurrentAlgoName = AlgorithmNiceHashNames.GetName(_currentAlgorithm.NiceHashID);
                _currentMiner.InitBenchmarkSetup(new MiningPair(_currentDevice, _currentAlgorithm));

                var time = ConfigManager.GeneralConfig.BenchmarkTimeLimits
                    .GetBenchamrktime(benchmarkOptions1.PerformanceType, _currentDevice.DeviceGroupType);
                //currentConfig.TimeLimit = time;
                if (__CPUBenchmarkStatus != null) {
                    __CPUBenchmarkStatus.Time = time;
                }
                
                // dagger about 4 minutes
                var showWaitTime = _currentAlgorithm.NiceHashID == AlgorithmType.DaggerHashimoto ? 4 * 60 : time;

                dotCount = 0;
                _benchmarkingTimer.Start();

                _currentMiner.BenchmarkStart(time, this);
                algorithmsListView1.SetSpeedStatus(_currentDevice, _currentAlgorithm.NiceHashID,
                    getDotsWaitString());
            } else {
                NextBenchmark();
            }

        }

        void EndBenchmark() {
            _benchmarkingTimer.Stop();
            _inBenchmark = false;
            Helpers.ConsolePrint("FormBenchmark", "EndBenchmark() benchmark routine finished");

            CopyBenchmarks();

            BenchmarkStoppedGUISettings();
            // check if all ok
            if(_benchmarkFailedAlgoPerDev.Count == 0 && StartMining == false) {
                MessageBox.Show(
                    International.GetText("FormBenchmark_Benchmark_Finish_Succes_MsgBox_Msg"),
                    International.GetText("FormBenchmark_Benchmark_Finish_MsgBox_Title"),
                    MessageBoxButtons.OK);
            } else if (StartMining == false) {
                var result = MessageBox.Show(
                    International.GetText("FormBenchmark_Benchmark_Finish_Fail_MsgBox_Msg"),
                    International.GetText("FormBenchmark_Benchmark_Finish_MsgBox_Title"),
                    MessageBoxButtons.RetryCancel);

                if (result == System.Windows.Forms.DialogResult.Retry) {
                    StartButonClick();
                    return;
                } else /*Cancel*/ {
                    // get unbenchmarked from criteria and disable
                    CalcBenchmarkDevicesAlgorithmQueue();
                    foreach (var deviceAlgoQueue in _benchmarkDevicesAlgorithmQueue) {
                        foreach (var algorithm in deviceAlgoQueue.Item2) {
                            algorithm.Skip = true;
                        }
                    }
                }
            }
            if (ExitWhenFinished || StartMining) {
                this.Close();
            }
        }


        public void SetCurrentStatus(string status) {
            this.Invoke((MethodInvoker)delegate {
                algorithmsListView1.SetSpeedStatus(_currentDevice, _currentAlgorithm.NiceHashID, getDotsWaitString());
            });
        }

        public void OnBenchmarkComplete(bool success, string status) {
            if (!_inBenchmark) return;
            this.Invoke((MethodInvoker)delegate {
                _bechmarkedSuccessCount += success ? 1 : 0;
                bool rebenchSame = false;
                if(success && __CPUBenchmarkStatus != null && CPUAlgos.Contains(_currentAlgorithm.NiceHashID)) {
                    if (__CPUBenchmarkStatus.HasAlreadyBenchmarked && __CPUBenchmarkStatus.BenchmarkSpeed > _currentAlgorithm.BenchmarkSpeed) {
                        rebenchSame = false;
                        _currentAlgorithm.BenchmarkSpeed = __CPUBenchmarkStatus.BenchmarkSpeed;
                        _currentAlgorithm.LessThreads--;
                    } else {
                        __CPUBenchmarkStatus.HasAlreadyBenchmarked = true;
                        __CPUBenchmarkStatus.BenchmarkSpeed = _currentAlgorithm.BenchmarkSpeed;
                        __CPUBenchmarkStatus.LessTreads++;
                        if(__CPUBenchmarkStatus.LessTreads < Globals.ThreadsPerCPU) {
                            _currentAlgorithm.LessThreads = __CPUBenchmarkStatus.LessTreads;
                            rebenchSame = true;
                        } else {
                            rebenchSame = false;
                        }
                    }
                }

                if(!rebenchSame) {
                    _benchmarkingTimer.Stop();
                }

                if (!success && !rebenchSame) {
                    // add new failed list
                    _benchmarkFailedAlgoPerDev.Add(
                        new DeviceAlgo() {
                            Device = _currentDevice.Name,
                            Algorithm = _currentAlgorithm.GetName()
                        } );
                    algorithmsListView1.SetSpeedStatus(_currentDevice, _currentAlgorithm.NiceHashID, status);
                } else if (!rebenchSame) {
                    // set status to empty string it will return speed
                    _currentAlgorithm.ClearBenchmarkPending();
                    algorithmsListView1.SetSpeedStatus(_currentDevice, _currentAlgorithm.NiceHashID, "");
                }
                if (rebenchSame) {
                    _currentMiner.BenchmarkStart(__CPUBenchmarkStatus.Time, this);
                } else {
                    NextBenchmark();
                }
            });
        }

        #region Benchmark progress GUI stuff

        private void SetLabelBenchmarkSteps(int current, int max) {
            labelBenchmarkSteps.Text = String.Format(International.GetText("FormBenchmark_Benchmark_Step"), current, max);
        }

        private void StepUpBenchmarkStepProgress() {
            SetLabelBenchmarkSteps(_bechmarkCurrentIndex + 1, _benchmarkAlgorithmsCount);
            progressBarBenchmarkSteps.Value = _bechmarkCurrentIndex + 1;
        }

        private void ResetBenchmarkProgressStatus() {
            progressBarBenchmarkSteps.Value = 0;
        }

        #endregion // Benchmark progress GUI stuff

        private void CloseBtn_Click(object sender, EventArgs e) {
            this.Close();
        }

        private void FormBenchmark_New_FormClosing(object sender, FormClosingEventArgs e) {
            if (_inBenchmark) {
                e.Cancel = true;
                return;
            }

            // disable all pending benchmark
            foreach (var cDev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                foreach (var algorithm in cDev.AlgorithmSettings.Values) {
                    algorithm.ClearBenchmarkPending();
                }
            }

            // save already benchmarked algorithms
            ConfigManager.CommitBenchmarks();
            // check devices without benchmarks
            foreach (var cdev in ComputeDeviceManager.Avaliable.AllAvaliableDevices) {
                if (cdev.Enabled) {
                    bool Enabled = false;
                    foreach (var algo in cdev.AlgorithmSettings) {
                        if (algo.Value.BenchmarkSpeed > 0) {
                            Enabled = true;
                            break;
                        }
                    }
                    cdev.Enabled = Enabled;
                }
            }
        }

        private void devicesListView1_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e) {
            //algorithmSettingsControl1.Deselect();
            // show algorithms
            var _selectedComputeDevice = ComputeDeviceManager.Avaliable.GetCurrentlySelectedComputeDevice(e.ItemIndex, true);
            algorithmsListView1.SetAlgorithms(_selectedComputeDevice, _selectedComputeDevice.Enabled);
        }

        private void radioButton_SelectedUnbenchmarked_CheckedChanged_1(object sender, EventArgs e) {
            _algorithmOption = AlgorithmBenchmarkSettingsType.SelectedUnbenchmarkedAlgorithms;
            CalcBenchmarkDevicesAlgorithmQueue();
            devicesListViewEnableControl1.ResetListItemColors();
            algorithmsListView1.ResetListItemColors();
        }

        private void radioButton_RE_SelectedUnbenchmarked_CheckedChanged(object sender, EventArgs e) {
            _algorithmOption = AlgorithmBenchmarkSettingsType.ReBecnhSelectedAlgorithms;
            CalcBenchmarkDevicesAlgorithmQueue();
            devicesListViewEnableControl1.ResetListItemColors();
            algorithmsListView1.ResetListItemColors();
        }

        private void checkBox_StartMiningAfterBenchmark_CheckedChanged(object sender, EventArgs e) {
            this.StartMining = this.checkBox_StartMiningAfterBenchmark.Checked;
        }

    }
}
