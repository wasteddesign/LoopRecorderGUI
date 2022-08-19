using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuzzGUI.Common.Settings;

namespace LoopRecorder.GUI
{
    public class LoopRecorderSettings : Settings
    {
        [BuzzSetting(16, Description = "Loop Length.", Minimum = 16, Maximum = 16*100)]
        public int LoopLength { get; set; }

        [BuzzSetting(1, Description = "Silent Seconds Before Next Record.", Minimum = 0, Maximum = 10)]
        public int SilentSeconds { get; set; }


        [BuzzSetting(false, Description = "Write To Wavetable.")]
        public bool WriteToWavetable { get; set; }

        [BuzzSetting(false, Description = "Overwrite Samples in Wavetable.")]
        public bool Overwrite { get; set; }

        [BuzzSetting(false, Description = "Start Recoding Immediately When Playing.")]
        public bool RecordImmediately { get; set; }

        [BuzzSetting(false, Description = "Record To Loop End Ignoring Loop Lenght Value.")]
        public bool RecordToLoopEnd { get; set; }

        [BuzzSetting(1, Description = "Default Wavetable Index.", Minimum = 1, Maximum = 200)]
        public int DefaultWaveTableIndex { get; set; }
    }
}
