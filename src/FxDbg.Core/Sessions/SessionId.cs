using System;

namespace FxDbg.Core.Sessions;

public sealed class SessionId : IEquatable<SessionId>
{
    public SessionId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A session ID cannot be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static SessionId New() => new(Guid.NewGuid());

    public bool Equals(SessionId? other) => other is not null && Value.Equals(other.Value);

    public override bool Equals(object? obj) => Equals(obj as SessionId);

    public override int GetHashCode() => Value.GetHashCode();

    public override string ToString() => Value.ToString("D");

    public static bool operator ==(SessionId? left, SessionId? right) => Equals(left, right);

    public static bool operator !=(SessionId? left, SessionId? right) => !Equals(left, right);
}
