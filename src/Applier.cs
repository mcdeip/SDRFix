using System;
using System.Threading;
using System.Windows.Forms;

namespace SdrBrightness
{
    sealed class ApplyRequest
    {
        public HdrDisplay Display;
        public int Value;
        public bool Final;    // also switch the color profile; otherwise brightness only (while sliding)
        public bool Hdr;      // the HDR content profile instead of the one for Value
        public string Gamma;  // which of setup's sets to use
        public bool Reload;   // the profile files were just rewritten: make Windows read them again
        public int Seq;
    }

    sealed class ApplyResult
    {
        public int Value;
        public string ActiveProfile;
        public string Error;
        public int Seq;
    }

    // Talks to Windows on a background thread so the slider never stalls.
    // Requests that pile up while one is running collapse into the newest.
    sealed class Applier
    {
        readonly Control ui;
        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        ApplyRequest pending;

        public event Action<ApplyResult> Applied;

        public Applier(Control ui)
        {
            this.ui = ui;
            new Thread(Run) { IsBackground = true, Name = "Applier" }.Start();
        }

        public void Submit(ApplyRequest request)
        {
            lock (gate)
            {
                // A newer request replaces an unstarted one, but must not lose its reload.
                if (pending != null && pending.Reload)
                    request.Reload = true;
                pending = request;
            }
            wake.Set();
        }

        void Run()
        {
            while (true)
            {
                wake.WaitOne();
                ApplyRequest request;
                lock (gate)
                {
                    request = pending;
                    pending = null;
                }
                if (request == null)
                    continue;

                ApplyResult result;
                try
                {
                    result = Apply(request);
                }
                catch (Exception ex)
                {
                    result = request.Final ? new ApplyResult { Value = request.Value, Error = ex.Message, Seq = request.Seq } : null;
                }
                if (result == null)
                    continue;

                try
                {
                    ui.BeginInvoke((Action)delegate
                    {
                        var handler = Applied;
                        if (handler != null)
                            handler(result);
                    });
                }
                catch (InvalidOperationException)
                {
                    // Shutting down.
                }
            }
        }

        static ApplyResult Apply(ApplyRequest r)
        {
            int err = r.Display.SetSdrWhiteLevel(HdrDisplay.LevelFromSlider(r.Value), r.Final);
            if (!r.Final)
                return null;

            var result = new ApplyResult { Value = r.Value, Seq = r.Seq };
            if (err != 0)
                result.Error = "Couldn't set brightness (error " + err + ")";

            string model = r.Display.ModelId;
            ColorProfile profile = r.Hdr ? ProfileLibrary.HdrFor(model) : ProfileLibrary.Find(model, r.Gamma, r.Value);
            string active = r.Display.GetDefaultProfile();
            bool selected = profile != null && string.Equals(active, profile.FileName, StringComparison.OrdinalIgnoreCase);
            // Re-selecting the active profile only makes Windows reload it, which is wanted only after a rewrite.
            if (profile != null && (!selected || r.Reload))
            {
                if (selected)
                {
                    // Windows rereads a profile when it's selected, not when it's already the default.
                    ColorProfile other = r.Hdr ? ProfileLibrary.Find(model, r.Gamma, r.Value)
                        : ProfileLibrary.Find(model, r.Gamma == ProfileGenerator.Gammas[0] ? ProfileGenerator.Gammas[1] : ProfileGenerator.Gammas[0], r.Value);
                    if (other == null || other == profile)
                        other = ProfileLibrary.HdrFor(model);
                    if (other != null && other != profile)
                        r.Display.SetDefaultProfile(other.FileName);
                }
                int hr = r.Display.SetDefaultProfile(profile.FileName);
                if (hr != 0 && result.Error == null)
                    result.Error = "Couldn't select " + profile.FileName + " (0x" + hr.ToString("X8") + ")";
                active = r.Display.GetDefaultProfile();
            }
            result.ActiveProfile = active;
            return result;
        }
    }
}
