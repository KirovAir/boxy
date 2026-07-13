namespace Boxy.Web.Models;

/// <summary>
/// Email configuration, bound from the "Email" section. <see cref="Provider"/> selects the backend so
/// more can be plugged in later (SendGrid, Mailgun, SES) by adding an <c>IEmailSender</c> and a case in
/// the DI switch - call sites never change. Only "smtp" is implemented today; "none" disables email.
/// </summary>
public class EmailSettings
{
    public const string SectionName = "Email";

    public string Provider { get; set; } = "none";
    public string From { get; set; } = "boxy@localhost";
    public string FromName { get; set; } = "Boxy";
    public SmtpSettings Smtp { get; set; } = new();
}

public class SmtpSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }

    /// <summary>Connection security: none | auto | starttls | ssl. "auto" lets the client pick.</summary>
    public string Security { get; set; } = "auto";
}
