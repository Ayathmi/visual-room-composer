namespace VisualRoomComposer.Core
{
    /// <summary>Modal file selection boundary; a null path means cancellation and errors remain explicit.</summary>
    public interface IFileDialogService
    {
        string OpenModel();
        string SaveFbx();
    }
}
