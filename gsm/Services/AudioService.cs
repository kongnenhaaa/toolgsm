using System.IO;

namespace gsm.Services
{
    public interface IAudioService
    {
        void Play(string filePath);
        void Stop();
    }

    public class AudioService : IAudioService
    {
        private System.Media.SoundPlayer? _player;

        public void Play(string filePath)
        {
            try
            {
                Stop();
                if (!File.Exists(filePath)) return;
                _player = new System.Media.SoundPlayer(filePath);
                _player.Play(); // async, không block
            }
            catch { }
        }

        public void Stop()
        {
            try { _player?.Stop(); } catch { }
            _player = null;
        }
    }
}
