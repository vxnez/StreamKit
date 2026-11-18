namespace StreamKit.Core.Events;

/// <summary>Identidad mínima de un espectador, tal como la entrega la plataforma.</summary>
public readonly record struct ViewerRef(string UserId, string UniqueId, string Nickname)
{
    public static ViewerRef Empty => new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrEmpty(UserId) && string.IsNullOrEmpty(UniqueId);

    /// <summary>Nombre visible, con fallback al handle y al id.</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Nickname) ? Nickname
        : !string.IsNullOrWhiteSpace(UniqueId) ? UniqueId
        : UserId;
}
