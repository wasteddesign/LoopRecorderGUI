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
using BuzzGUI.Common.DSP;
using libsndfile;
using WDE.EasyRec;
using System.Globalization;

namespace LoopRecorder.GUI
{
	public class ConnectionVM : INotifyPropertyChanged
	{
		public enum States { WaitingForSignal, Recording, WaitingForSilence,
            WaitingForStop
        }
        States state = States.WaitingForSignal;
		public States State 
		{
			get { return state; }
			set
			{	
				state = value;
				switch (state)
				{
					case States.WaitingForSignal: NameBrush = Brushes.WhiteSmoke; break;
					case States.Recording: NameBrush = Brushes.Red; break;
					case States.WaitingForSilence: NameBrush = Brushes.Blue; break;
					case States.WaitingForStop: NameBrush = Brushes.LightGreen; break;
				}
				PropertyChanged.Raise(this, "NameBrush");
			}
		}

		public LoopRecorderGUI GUI { get; set; }
		public IMachineConnection MachineConnection { get; set; }
		public string Name 
		{ 
			get 
			{
				if (MachineConnection != null)
					return MachineConnection.Source.Name + " -> " + MachineConnection.Destination.Name;
				else
					return "Master";
			} 
		}
		public Brush NameBrush { get; set; }

		bool isSelected;
		public bool IsSelected
		{
			get { return isSelected; }
			set
			{
				if (value == isSelected) return;

				isSelected = value;

				if (MachineConnection != null)
				{
					if (isSelected)
						MachineConnection.Tap += MachineConnection_Tap;
					else
						MachineConnection.Tap -= MachineConnection_Tap;
				}
				else if (GUI.Machine != null)
				{
					if (isSelected)
						GUI.Machine.Graph.Buzz.MasterTap += MachineConnection_Tap;
					else
						GUI.Machine.Graph.Buzz.MasterTap -= MachineConnection_Tap;
				}

				PropertyChanged.Raise(this, "VUMeterVisibility");
			}
		}



		SampleData sampleData;

		int myWavetableIndex = -1;
        private string timestamp;
        float maxSample;
		public double VUMeterLevel { get; set; }
		public Visibility VUMeterVisibility { get { return isSelected ? Visibility.Visible : Visibility.Collapsed; } }
		const double VUMeterRange = 80.0;

		internal int GetWavetableIndex()
        {
			int myIndex = -1;

			if (IsSelected)
            {
				myIndex = GUI.WavetableIndex < 1 ? 0 : GUI.WavetableIndex - 1;				

				foreach (ConnectionVM cvm in GUI.connections)
                {
					if (cvm.IsSelected && cvm != this)
					{
						myIndex++;
					}
					else if (cvm == this)
					{
						break;
					}
                }
			}

			return myIndex;
        }

		public void TimerUpdate()
		{
			if (maxSample >= 0)
			{
				var db = Math.Min(Math.Max(Decibel.FromAmplitude(maxSample), -VUMeterRange), 0.0);
				VUMeterLevel = (db + VUMeterRange) / VUMeterRange;
				PropertyChanged.Raise(this, "VUMeterLevel");
				maxSample = -1;
			}
		}

		void MachineConnection_Tap(float[] samples, bool stereo, SongTime songtime)
		{
			maxSample = Math.Max(maxSample, DSP.AbsMax(samples) * (1.0f / 32768.0f));

			// call ProcessBuffer in a background task
			GUI.bufferQueue.Add(Tuple.Create(this, samples, stereo, songtime));
		}

		int fileCount = 0;

		string NextFilename
		{
			get
			{
				string path;

				do
				{
					fileCount++;
					path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GUI.OutputPath), System.IO.Path.GetFileNameWithoutExtension(GUI.OutputPath));
					
					if (MachineConnection != null)
						path += " - " + MachineConnection.Source.Name + " - " + MachineConnection.Destination.Name + " - " + fileCount.ToString() + ".wav";
					else
						path += " - " + fileCount.ToString() + ".wav";

				} while (File.Exists(path));

				return path;
			}
		}

		SoundFile soundFile;
		int framesLeft;
		public float SilentSeconds = (float)LoopRecorderGUI.Settings.SilentSeconds;

		void FinishRecording(SongTime songtime)
		{
			if (GUI.IsSelectedWriteToWavetable)
			{
				sampleData.TrimBuffer();
				float[] buffer = sampleData.GetBuffer();
				sampleData.Init();
				Application.Current.Dispatcher.BeginInvoke(new Action(() =>
				{
					if (!GUI.IsSelectedOverwrite)
                    {
						myWavetableIndex = FindNextAvailableIndex(myWavetableIndex);
					}

					if ( (myWavetableIndex != -1) && (myWavetableIndex < 200) )
						saveToWavetable(myWavetableIndex, ref buffer);
				}));
			}
			else
			{
				soundFile.Close();
				soundFile.Dispose();
				soundFile = null;
			}
			if (GUI.IsRecordImmediately)
			{
				State = States.WaitingForStop;
			}
			else
			{
				State = States.WaitingForSilence;
			}
			framesLeft = (int)(songtime.SamplesPerSec * SilentSeconds);
		}

        private int FindNextAvailableIndex(int myWavetableIndex)
        {
			IWavetable wt = Global.Buzz.Song.Wavetable;

			int i;
			for (i = myWavetableIndex; i < 200; i++)
			{
				if (wt.Waves[i] == null)
					break;
			}

			return (i == 200) || (myWavetableIndex > 199) ? -1 : i;
        }

        void Record(float[] samples, int offset, int nframes, SongTime songtime)
		{
			int n = Math.Min(nframes, framesLeft);

			if (n > 0)
			{
				if (GUI.IsSelectedWriteToWavetable)
				{
					sampleData.AppendSamples(samples, offset, n);
				}
				else
				{
					soundFile.WriteFloat(DSP.ScaledCopy(samples, 1.0f / 32768.0f), offset, n);
				}
				framesLeft -= n;
			}

			if (framesLeft <= 0) FinishRecording(songtime);
		}
		
		public void ProcessBuffer(float[] samples, bool stereo, SongTime songtime)
		{
			int nframes = stereo ? samples.Length / 2 : samples.Length;

			if (State == States.WaitingForSignal)
			{
				if (GUI.Started)
				{
					int offset = DSP.FirstNonZeroOffset(samples);

					if (GUI.IsRecordImmediately)
                    {
						offset = -1;
						if (Global.Buzz.Playing && songtime.PosInSubTick == 0 && songtime.PosInTick == 0)
						{
							offset = 0;
						}
					}

					if (offset >= 0)
					{
						if (stereo) offset /= 2;
						nframes -= offset;

						try
						{
							if (Global.Buzz.Playing && GUI.IsRecordImmediately && GUI.IsRecordToLoopEnd)
                            {
								framesLeft = (int)((Global.Buzz.Song.LoopEnd - Global.Buzz.Song.PlayPosition) * songtime.AverageSamplesPerTick);
							}
							else
								framesLeft = (int)(songtime.AverageSamplesPerTick * GUI.LoopLength);

							if (GUI.IsSelectedWriteToWavetable)
							{
								myWavetableIndex = GetWavetableIndex();
								timestamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture); // <-- Force colon
								sampleData = new SampleData();
							}
							else
							{
								soundFile = SoundFile.Create(NextFilename, songtime.SamplesPerSec, stereo ? 2 : 1, Format.SF_FORMAT_WAV | Format.SF_FORMAT_PCM_32);
								soundFile.Clipping = true;
							}
							State = States.Recording;
							Record(samples, stereo ? offset * 2 : offset, nframes, songtime);
						}
						catch (Exception e) { GUI.Machine.Graph.Buzz.DCWriteLine(e.Message); }
					}
				}
			}
			else if (State == States.Recording)
			{
				if (GUI.Started)
				{
					try
					{
						Record(samples, 0, nframes, songtime);
					}
					catch (Exception e) { GUI.Machine.Graph.Buzz.DCWriteLine(e.Message); }
				}
				else
				{
					try
					{
						FinishRecording(songtime);
					}
					catch (Exception e) { GUI.Machine.Graph.Buzz.DCWriteLine(e.Message); }
				}
			}
			else if (State == States.WaitingForSilence)
			{
				int offset = DSP.FirstNonZeroOffset(samples);
				if (offset >= 0)
				{
					framesLeft = (int)(songtime.SamplesPerSec * SilentSeconds);
				}
				else
				{
					framesLeft -= nframes;
					if (framesLeft <= 0)
					{
						framesLeft = 0;
						State = States.WaitingForSignal;
					}
				}
				
			}
			else if (State == States.WaitingForStop)
            {
				if (!Global.Buzz.Playing)
				{
					GUI.Started = false;
					PropertyChanged.Raise(GUI, "StartStopButtonText");
					PropertyChanged.Raise(GUI, "SettingsGUIEnabled");
					State = States.WaitingForSignal;
				}
			}

		}

		public void saveToWavetable(int slot, ref float[] buffer)
		{
			var wt = Global.Buzz.Song.Wavetable;
			WaveFormat wf;
			wf = WaveFormat.Float32;

			string name = MachineConnection != null ? MachineConnection.Source.Name + " - " + MachineConnection.Destination.Name : "Master";

			// write to wavetable
			int rootnote = BuzzNote.FromMIDINote(48);
			wt.AllocateWave(slot,
								"",
								name + " " + timestamp,
								(int)(buffer.Length / 2), // Stereo --> divide by 2
								wf,
								true,
								rootnote,
								false,
								true);
			IWaveLayer layer = wt.Waves[slot].Layers.Last();
			layer.SampleRate = Global.Buzz.SelectedAudioDriverSampleRate;

			for (int i = 0; i < buffer.Length; i++)
			{
				buffer[i] = buffer[i] / 32768.0f;
			}

			layer.SetDataAsFloat(buffer, 0, 2, 0, 0, (int)buffer.Length / 2); // Left
			layer.SetDataAsFloat(buffer, 1, 2, 1, 0, (int)buffer.Length / 2); // Right
			layer.LoopStart = 0;
			layer.LoopEnd = buffer.Length / 2;

			layer.InvalidateData();
		}


		#region INotifyPropertyChanged Members

		public event PropertyChangedEventHandler PropertyChanged;

		#endregion
	}
}
