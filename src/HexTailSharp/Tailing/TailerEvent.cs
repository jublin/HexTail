namespace HexTailSharp.Tailing;

/// <summary>
/// Immutable event emitted by a file tailer and consumed by the state/UI layer
/// via <see cref="TailerService.Events"/>. Tailers never touch UI state directly.
/// </summary>
/// <param name="FileId">Identifier of the tailed file, as assigned by the caller of <see cref="TailerService.StartTailer"/>.</param>
public abstract record TailerEvent(string FileId);

/// <summary>
/// One or more complete lines were read from the file. Lines are emitted in file order;
/// an incomplete trailing line at EOF is held back until it is completed by a newline.
/// </summary>
public sealed record NewLines(string FileId, IReadOnlyList<string> Lines) : TailerEvent(FileId);

/// <summary>
/// The file was rotated: it was deleted or renamed and then recreated.
/// The consumer should clear its buffer; subsequent <see cref="NewLines"/> events
/// carry the content of the new file.
/// </summary>
public sealed record FileRotated(string FileId) : TailerEvent(FileId);

/// <summary>
/// The file shrank below the last read offset. The consumer should clear its buffer;
/// tailing resumes from the start of the truncated file.
/// </summary>
public sealed record FileTruncated(string FileId) : TailerEvent(FileId);
