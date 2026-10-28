// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.IdentityAndAccess;

public sealed class UserAccountAuditLogTests
{
    [Theory]
    [InlineData("c345ffcd-844a-4dd7-a677-25bc1774d052")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ProvisionedUserCreated_LogsAccountIdentifiersWithoutPrivateIdentityFields(string actorId)
    {
        var actorUserId = Guid.Parse(actorId);
        var userId = Guid.NewGuid();
        const string issuer = "https://identity.example.test/realms/customers";
        var logger = new CapturingLogger();
        var auditLog = new UserAccountAuditLog(logger);

        auditLog.ProvisionedUserCreated(actorUserId, userId, issuer);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal("user.provisioned", entry.Properties["AuditEvent"]);
        Assert.Equal(actorUserId, entry.Properties["ActorUserId"]);
        Assert.Equal(userId, entry.Properties["TargetUserId"]);
        Assert.Equal(issuer, entry.Properties["Issuer"]);
        Assert.Equal(
            ["ActorUserId", "AuditEvent", "Issuer", "TargetUserId", "{OriginalFormat}"],
            entry.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal($"user.provisioned actor={actorUserId} target={userId} issuer={issuer}", entry.Message);
    }

    private sealed class CapturingLogger : ILogger<UserAccountAuditLog>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToDictionary();
            this.Entries.Add(new LogEntry(logLevel, properties, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, IReadOnlyDictionary<string, object?> Properties, string Message, Exception? Exception);
}
