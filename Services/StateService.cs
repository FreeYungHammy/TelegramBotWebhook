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
        private readonly ConcurrentDictionary<long, string> _groupCompanyMap;
        private readonly ConcurrentDictionary<long, bool> _awaitingCompanyId = new();
        private readonly ConcurrentDictionary<long, string> _awaitingOrderId = new();
        private readonly Dictionary<long, string> awaitingBlacklistType = new();

        public void SetAwaitingBlacklistType(long chatId, string type) => awaitingBlacklistType[chatId] = type;
        public bool IsAwaitingBlacklist(long chatId) => awaitingBlacklistType.ContainsKey(chatId);
        public string GetBlacklistType(long chatId) => awaitingBlacklistType.TryGetValue(chatId, out var type) ? type : null;
        public void ClearBlacklist(long chatId) => awaitingBlacklistType.Remove(chatId);

        public StateService(string storagePath)
        {
            _storagePath = storagePath;

            if (!File.Exists(_storagePath))
                File.Create(_storagePath).Close();

            _groupCompanyMap = new ConcurrentDictionary<long, string>(
                File.ReadAllLines(_storagePath)
                    .Select(line => line.Split(','))
                    .Where(parts => parts.Length == 2 && long.TryParse(parts[0], out _))
                    .ToDictionary(parts => long.Parse(parts[0]), parts => parts[1].Trim())
            );
        }

        // Company ID Registration Flow
        public bool IsWaitingForCompanyId(long chatId) => _awaitingCompanyId.ContainsKey(chatId);

        public void SetAwaitingCompanyId(long chatId)
        {
            _awaitingCompanyId[chatId] = true;
        }

        public void RegisterCompanyId(long chatId, string companyId)
        {
            _awaitingCompanyId.TryRemove(chatId, out _);
            _groupCompanyMap[chatId] = companyId;

            File.AppendAllText(_storagePath, $"{chatId},{companyId}{Environment.NewLine}");
        }

        // Order ID Flow
        public bool IsWaitingForOrderId(long chatId) => _awaitingOrderId.ContainsKey(chatId);

        public void SetAwaitingOrderId(long chatId, string companyId)
        {
            _awaitingOrderId[chatId] = companyId;
        }

        public void ClearAwaitingOrderId(long chatId)
        {
            _awaitingOrderId.TryRemove(chatId, out _);
        }

        // Read
        public string? GetCompanyId(long chatId)
        {
            return _groupCompanyMap.TryGetValue(chatId, out var companyId) ? companyId : null;
        }
    }
}
