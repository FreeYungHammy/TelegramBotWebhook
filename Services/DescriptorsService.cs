using System;
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
            var baseDir = AppContext.BaseDirectory;
            _filePath = Path.Combine(baseDir, "descriptors.txt");
        }

        public async Task<string> GetDescriptorsAsync()
        {
            try
            {
                _logger.LogInformation("Reading descriptor contents from file at: {Path}", _filePath);

                if (!File.Exists(_filePath))
                {
                    _logger.LogWarning("Descriptors file not found at path: {Path}", _filePath);
                    return "Descriptors file is missing.";
                }

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
