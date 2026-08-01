using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VoicePlugin.Views
{
    public sealed class PronunciationRuleRow : INotifyPropertyChanged
    {
        private string _original = string.Empty;
        private string _replacement = string.Empty;

        public string Original
        {
            get => _original;
            set
            {
                if (_original == value) return;
                _original = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string Replacement
        {
            get => _replacement;
            set
            {
                if (_replacement == value) return;
                _replacement = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
