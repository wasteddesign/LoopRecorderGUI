using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Collections.ObjectModel;
using Microsoft.Win32;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using Wintellect.PowerCollections;

namespace LoopRecorder.GUI
{
	public class MachineGUIFactory : IMachineGUIFactory
	{
		public IMachineGUI CreateGUI(IMachineGUIHost host) { return new LoopRecorderGUI(); }
	}

	public partial class LoopRecorderGUI : UserControl, IMachineGUI, INotifyPropertyChanged
	{
		DispatcherTimer timer;
		public ObservableCollection<ConnectionVM> connections = new ObservableCollection<ConnectionVM>();
		internal BlockingCollection<Tuple<ConnectionVM, float[], bool, SongTime>> bufferQueue = new BlockingCollection<Tuple<ConnectionVM, float[], bool, SongTime>>();

		public string OutputPath { get; set; }
		public SimpleCommand BrowseCommand { get; private set; }
		public SimpleCommand StartStopCommand { get; private set; }

		public int LoopLength { get; set; }

		public bool IsSelectedOverwrite { get; set; }
		public bool IsRecordImmediately { get; set; }
		public bool IsRecordToLoopEnd { get; set; }
		public bool IsSelectedWriteToWavetable { get; set; }

		public int WavetableIndex { get; set; }
		public bool Started { get; set; }

		public string StartStopButtonText {	get { return Started ? "Stop" : "Start"; } }
		public bool SettingsGUIEnabled { get { return !Started; } }

		public static LoopRecorderSettings Settings = new LoopRecorderSettings();

		public LoopRecorderGUI()
		{
			LoopLength = LoopRecorderGUI.Settings.LoopLength;
			WavetableIndex = LoopRecorderGUI.Settings.DefaultWaveTableIndex;
			IsSelectedOverwrite = LoopRecorderGUI.Settings.Overwrite;
			IsSelectedWriteToWavetable = LoopRecorderGUI.Settings.WriteToWavetable;
			IsRecordImmediately = LoopRecorderGUI.Settings.RecordImmediately;
			IsRecordToLoopEnd = LoopRecorderGUI.Settings.RecordToLoopEnd;

			OutputPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Loop.wav");

			BrowseCommand = new SimpleCommand
			{
				CanExecuteDelegate = x => true,
				ExecuteDelegate = x =>
				{
					var dlg = new SaveFileDialog();
					dlg.InitialDirectory = System.IO.Path.GetDirectoryName(OutputPath);
					dlg.FileName = System.IO.Path.GetFileName(OutputPath);
					dlg.Filter = "Wav|*.wav";
					if ((bool)dlg.ShowDialog())
					{
						OutputPath = dlg.FileName;
						PropertyChanged.Raise(this, "OutputPath");
					}
				}
			};

			StartStopCommand = new SimpleCommand
			{
				CanExecuteDelegate = x => true,
				ExecuteDelegate = x =>
				{
					Started ^= true;
					PropertyChanged.Raise(this, "StartStopButtonText");
					PropertyChanged.Raise(this, "SettingsGUIEnabled");
				}
			};

			DataContext = this;
			InitializeComponent();

			var cvs = new CollectionViewSource();
			cvs.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Ascending));
			cvs.Source = connections;
			connectionList.DataContext = cvs;

			Task.Factory.StartNew(() =>
			{
				while (!bufferQueue.IsCompleted)
				{
					try
					{
						var x = bufferQueue.Take();
						x.Item1.ProcessBuffer(x.Item2, x.Item3, x.Item4);
					}
					catch (InvalidOperationException) { }
				}

			});

			this.IsVisibleChanged += (sender, e) =>
			{
				if (IsVisible && timer == null)
				{
					SetTimer();
				}
				else if (!IsVisible && timer != null)
				{
					timer.Stop();
					timer = null;
				}
			};

			// master output tap
			connections.Add(new ConnectionVM() { GUI = this, MachineConnection = null, State = ConnectionVM.States.WaitingForSignal });
		}

        private void InitLoopRecorderSettingsUI()
        {
			bool settingsViewContainsLoopRecorder = false;

			foreach (Dictionary<string, string> dic in BuzzGUI.Common.SettingsWindow.GetSettings())
			{
				if (dic.ContainsKey("LoopRecorder/LoopLength"))
					settingsViewContainsLoopRecorder = true;
			}

            LoopRecorderGUI.Settings.PropertyChanged += Settings_PropertyChanged;
			BuzzGUI.Common.SettingsWindow.AddSettings("LoopRecorder", Settings);

			if (Machine.Graph.Buzz.IsSettingsWindowVisible && !settingsViewContainsLoopRecorder)
			{
				// Redraw UI to display settings
				Machine.Graph.Buzz.IsSettingsWindowVisible = false;
				Machine.Graph.Buzz.IsSettingsWindowVisible = true;
			}
		}

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {

			if (e.PropertyName == "SilentSeconds")
			{
				foreach (ConnectionVM cvm in connections)
                {
					cvm.SilentSeconds = LoopRecorderGUI.Settings.SilentSeconds;

				}
			}
		}

        void SetTimer()
		{
			timer = new DispatcherTimer();
			timer.Interval = TimeSpan.FromMilliseconds(20);
			timer.Tick += (sender, e) => 
			{
				foreach (var c in connections)
					c.TimerUpdate();
			};
			timer.Start();
		}

		IMachine machine;
		public IMachine Machine
		{
			get { return machine; }
			set
			{
				if (machine != null)
				{
					machine.PropertyChanged -= new PropertyChangedEventHandler(machine_PropertyChanged);
					machine.Graph.ConnectionAdded -= new Action<IMachineConnection>(Graph_ConnectionAdded);
					machine.Graph.ConnectionRemoved -= new Action<IMachineConnection>(Graph_ConnectionRemoved);

					foreach (var c in connections) c.IsSelected = false;	// unsubscribe Tap event
					bufferQueue.CompleteAdding();
				}

				machine = value;

				if (machine != null)
				{
					machine.PropertyChanged += new PropertyChangedEventHandler(machine_PropertyChanged);
					machine.Graph.ConnectionAdded += new Action<IMachineConnection>(Graph_ConnectionAdded);
					machine.Graph.ConnectionRemoved += new Action<IMachineConnection>(Graph_ConnectionRemoved);

					foreach (var m in machine.Graph.Machines)
						foreach (var c in m.Inputs)
							Graph_ConnectionAdded(c);

					InitLoopRecorderSettingsUI();
				}
			}
		}

		void Graph_ConnectionAdded(IMachineConnection mc)
		{
			connections.Add(new ConnectionVM() { GUI = this, MachineConnection = mc, State = ConnectionVM.States.WaitingForSignal } );
		}

		void Graph_ConnectionRemoved(IMachineConnection mc)
		{
			var c = connections.Where(x => x.MachineConnection == mc).FirstOrDefault();
			if (c == null) return;
			connections.Remove(c);
		}

		void machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
		{
		}

		#region INotifyPropertyChanged Members

		public event PropertyChangedEventHandler PropertyChanged;

		#endregion

	}
}
