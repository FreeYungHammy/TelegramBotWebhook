using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TelegramBot_v2.Services
{
    public class DescriptorsService
    {
        private readonly ILogger<DescriptorsService> _logger;
        private readonly string _filePath;

        public DescriptorsService(ILogger<DescriptorsService> logger)
        {
            _logger = logger;
            _filePath = Path.Combine("/home/site/wwwroot", "descriptors.txt"); // adjust path if needed
        }

        public async Task<string> GetDescriptorsAsync()
        {
            try
            {
                _logger.LogInformation("Reading descriptor contents from file.");
                return await File.ReadAllTextAsync(_filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read descriptors file.");
                return "Failed to retrieve descriptor information.";
            }
        }
    }
}
