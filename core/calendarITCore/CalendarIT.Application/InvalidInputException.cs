namespace CalendarIT.Application;

/// <summary>
/// Input the caller can fix — a repeat rule that doesn't parse, an .ics file that isn't one. The
/// host turns it into a 400 with <see cref="Exception.Message"/> as the detail, so the message is
/// written for the person who sent the request and must not carry internals.
/// </summary>
public sealed class InvalidInputException(string message) : Exception(message);
