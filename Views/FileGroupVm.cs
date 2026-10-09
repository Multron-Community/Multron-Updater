using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using MultronUpdater.Services;

namespace MultronUpdater.Views
{
    public sealed class FileGroupVm : INotifyPropertyChanged
    {
        private string _name;
        private bool _enabled;
        private bool _restartProgram;
        private bool _refreshEdge;
        private bool _isExpanded = true;
        private bool _isDropTarget;

        public FileGroupVm(FileGroup g)
        {
            Id = g.Id;
            _name = g.Name;
            _enabled = g.Enabled;
            _restartProgram = g.RestartProgram;
            _refreshEdge = g.RefreshEdge;
            foreach (var f in g.Files) Add(new FileItem(f.Path, f.Include));
            Files.CollectionChanged += (_, _) => RaiseCounts();
        }

        public string Id { get; }
        public ObservableCollection<FileItem> Files { get; } = new();

        public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
        public bool Enabled { get => _enabled; set => Set(ref _enabled, value, nameof(Enabled)); }
        public bool RestartProgram { get => _restartProgram; set => Set(ref _restartProgram, value, nameof(RestartProgram)); }
        public bool RefreshEdge { get => _refreshEdge; set => Set(ref _refreshEdge, value, nameof(RefreshEdge)); }
        public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value, nameof(IsExpanded)); }
        public bool IsDropTarget { get => _isDropTarget; set => Set(ref _isDropTarget, value, nameof(IsDropTarget)); }

        public string CountText
        {
            get
            {
                int total = Files.Count, on = Files.Count(f => f.Include);
                int changed = Files.Count(f => f.Include && f.State is FileState.Changed or FileState.New);
                var text = total == 0 ? "empty – drop files here" : $"{on} of {total} checked";
                return changed > 0 ? $"{text} · {changed} to update" : text;
            }
        }

        public bool? AllChecked
        {
            get
            {
                if (Files.Count == 0) return false;
                int on = Files.Count(f => f.Include);
                return on == 0 ? false : on == Files.Count ? true : null;
            }
            set
            {
                bool v = value == true;
                foreach (var f in Files) f.Include = v;
            }
        }

        public void Add(FileItem item)
        {
            item.PropertyChanged += OnItemChanged;
            Files.Add(item);
        }

        public void Remove(FileItem item)
        {
            item.PropertyChanged -= OnItemChanged;
            Files.Remove(item);
        }

        public void Sort()
        {
            var sorted = Files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int old = Files.IndexOf(sorted[i]);
                if (old != i) Files.Move(old, i);
            }
        }

        public FileGroup ToModel() => new()
        {
            Id = Id,
            Name = string.IsNullOrWhiteSpace(Name) ? "Group" : Name.Trim(),
            Enabled = Enabled,
            RestartProgram = RestartProgram,
            RefreshEdge = RefreshEdge,
            Files = Files.Select(f => new TrackedFile { Path = f.Path, Include = f.Include }).ToList()
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action? Changed;

        private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            RaiseCounts();
            if (e.PropertyName == nameof(FileItem.Include)) Changed?.Invoke();
        }

        private void RaiseCounts()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AllChecked)));
        }

        private void Set<T>(ref T field, T value, string name)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name is not (nameof(IsExpanded) or nameof(IsDropTarget))) Changed?.Invoke();
        }
    }
}
