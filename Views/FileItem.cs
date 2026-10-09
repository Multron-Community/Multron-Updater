using System.ComponentModel;
using MultronUpdater.Services;

namespace MultronUpdater.Views
{
    public sealed class FileItem : INotifyPropertyChanged
    {
        private bool _include;
        private FileState _state;

        public FileItem(string path, bool include, FileState state = FileState.Unknown)
        {
            Path = path.Replace('\\', '/').Trim('/');
            _include = include;
            _state = state;
        }

        public string Path { get; }

        public string Folder
        {
            get
            {
                var i = Path.LastIndexOf('/');
                return i < 0 ? "" : Path[..(i + 1)].Replace('/', '\\');
            }
        }

        public string Name
        {
            get
            {
                var i = Path.LastIndexOf('/');
                return i < 0 ? Path : Path[(i + 1)..];
            }
        }

        public bool Include
        {
            get => _include;
            set { if (_include == value) return; _include = value; Raise(nameof(Include)); }
        }

        public FileState State
        {
            get => _state;
            set { if (_state == value) return; _state = value; Raise(nameof(State)); Raise(nameof(StateText)); }
        }

        private bool _isVisible = true;

        public bool IsVisible
        {
            get => _isVisible;
            set { if (_isVisible == value) return; _isVisible = value; Raise(nameof(IsVisible)); }
        }

        public string StateText => _state switch
        {
            FileState.Changed => "Changed on GitHub",
            FileState.New => "New (not on this computer yet)",
            FileState.UpToDate => "Up to date",
            FileState.NotOnGitHub => "Not found on GitHub",
            _ => "Not checked yet – click \"Load from GitHub\""
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
