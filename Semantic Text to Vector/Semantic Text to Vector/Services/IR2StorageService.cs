namespace Semantic_Text_to_Vector.Services
{
    public interface IR2StorageService
    {
        Task<IEnumerable<string>> GetFoldersAsync();
    }
}
