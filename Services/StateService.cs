using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TelegramBot_v2.Services
{
    public class StateService
    {
        private readonly string _storagePath;
        private readonly ConcurrentDictionary<long, bool> _awaitingCompanyId = new();
        private readonly ConcurrentDictionary<long, bool> _awaitingOrderInput = new();
        private readonly Dictionary<long, string> awaitingBlacklistType = new();
        private readonly ConcurrentDictionary<long, bool> _awaitingDescriptorSearch = new();

        // ----- Blacklist State -----
        public void SetAwaitingBlacklistType(long chatId, string type) => awaitingBlacklistType[chatId] = type;
        public bool IsAwaitingBlacklist(long chatId) => awaitingBlacklistType.ContainsKey(chatId);
        public string GetBlacklistType(long chatId) => awaitingBlacklistType.TryGetValue(chatId, out var type) ? type : null;
        public void ClearBlacklist(long chatId) => awaitingBlacklistType.Remove(chatId);

        // ----- Descriptor Search State -----
        public void SetAwaitingDescriptorSearch(long chatId) => _awaitingDescriptorSearch[chatId] = true;
        public bool IsAwaitingDescriptorSearch(long chatId) => _awaitingDescriptorSearch.ContainsKey(chatId);
        public void ClearAwaitingDescriptorSearch(long chatId) => _awaitingDescriptorSearch.TryRemove(chatId, out _);

        public StateService(string storagePath)
        {
            _storagePath = storagePath;

            if (!File.Exists(_storagePath))
                File.Create(_storagePath).Close();
        }

        // ----- Company ID Registration Flow -----
        public bool IsWaitingForCompanyId(long chatId) => _awaitingCompanyId.ContainsKey(chatId);
        public void SetAwaitingCompanyId(long chatId) => _awaitingCompanyId[chatId] = true;

        public void RegisterCompanyId(long chatId, string companyId)
        {
            _awaitingCompanyId.TryRemove(chatId, out _);
            File.AppendAllText(_storagePath, $"{chatId},{companyId}{Environment.NewLine}");
        }

        // ----- Order ID Flow -----
        public bool IsAwaitingOrderEntry(long chatId) => _awaitingOrderInput.ContainsKey(chatId);
        public void SetAwaitingOrderEntry(long chatId) => _awaitingOrderInput[chatId] = true;
        public void ClearAwaitingOrderEntry(long chatId) => _awaitingOrderInput.TryRemove(chatId, out _);

        // ----- Lookup -----
        public List<string> GetCompanyIdsForGroup(long chatId)
        {
            return File.ReadAllLines(_storagePath)
                       .Where(line => line.StartsWith(chatId + ","))
                       .Select(line => line.Split(',')[1].Trim())
                       .Distinct()
                       .ToList();
        }

        public string? GetCompanyId(long chatId)
        {
            return GetCompanyIdsForGroup(chatId).LastOrDefault(); // get the most recent one
        }
    }
}
