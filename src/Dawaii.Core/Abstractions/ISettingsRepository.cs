using System.Collections.Generic;

namespace Dawaii.Core.Abstractions
{
    public interface ISettingsRepository
    {
        string Get(string key);
        IReadOnlyDictionary<string, string> GetAll();
        void Set(string key, string value);
    }
}
