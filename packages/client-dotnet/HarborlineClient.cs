using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json.Serialization;
using Harborline.Api.Contracts;

namespace Harborline.Api.Protocol.Client;

/// <summary>The authenticated loopback session established for a Harborline App boot.</summary>
public sealed record SessionBootstrap(Uri NodeAddress);

/// <summary>Server-derived atomic permissions for the current Harborline App session.</summary>
public sealed record SessionPermissionSnapshot(
    [property: JsonPropertyName("effectivePermissions")] IReadOnlySet<string> EffectivePermissions);

/// <summary>Permission names consumed by Harborline App screens; the node remains the authority.</summary>
public static class HarborlinePermissions
{
    /// <summary>Read contact records.</summary>
    public const string ContactsRead = "contacts:read";
    /// <summary>Create contact records.</summary>
    public const string ContactsCreate = "contacts:create";
    /// <summary>Read scheduling records.</summary>
    public const string SchedulingRead = "scheduling:read";
    /// <summary>Author scheduling definitions and events.</summary>
    public const string SchedulingAuthor = "scheduling:author";
    /// <summary>Read invoice records.</summary>
    public const string RecordsRead = "records:read";
    /// <summary>Mutate invoice records.</summary>
    public const string RecordsWrite = "records:write";
}

/// <summary>Localized client messages shared by Harborline App screens.</summary>
public static class HarborlineClientMessages
{
    /// <summary>The fail-closed permission denial message.</summary>
    public const string PermissionDenied = "You do not have permission to perform this action.";
}

/// <summary>A node mutation was refused by the server-side permission evaluator.</summary>
public sealed class HarborlinePermissionDeniedException(string? permission = null) : Exception(
    string.IsNullOrWhiteSpace(permission)
        ? "You do not have permission to perform this action."
        : $"You do not have permission to perform '{permission}'.")
{
    /// <summary>The denied atomic permission, when the node returned it.</summary>
    public string? Permission { get; } = permission;
}

/// <summary>The minimal contact representation used by the W2 acceptance round trip.</summary>
public sealed record Contact(
    [property: JsonPropertyName("contactId")] string ContactId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("doNotContact")] bool DoNotContact = false);

#pragma warning disable CS1591

/// <summary>Full contact detail returned by the local-node contacts API.</summary>
public sealed record ContactDetail(
    [property: JsonPropertyName("contactId")] string ContactId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("legalName")] string? LegalName,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("webSite")] string? WebSite,
    [property: JsonPropertyName("tags")] string[] Tags,
    [property: JsonPropertyName("doNotContact")] bool DoNotContact,
    [property: JsonPropertyName("doNotEmail")] bool DoNotEmail,
    [property: JsonPropertyName("doNotCall")] bool DoNotCall,
    [property: JsonPropertyName("doNotSms")] bool DoNotSms,
    [property: JsonPropertyName("emails")] ContactEmail[] Emails,
    [property: JsonPropertyName("phones")] ContactPhone[] Phones,
    [property: JsonPropertyName("addresses")] ContactAddress[] Addresses,
    [property: JsonPropertyName("roles")] ContactRole[] Roles,
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt,
    [property: JsonPropertyName("version")] long Version);

public sealed record ContactEmail(
    [property: JsonPropertyName("emailId")] string EmailId,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("label")] string? Label);

public sealed record ContactPhone(
    [property: JsonPropertyName("phoneId")] string PhoneId,
    [property: JsonPropertyName("e164")] string E164,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("isMobile")] bool IsMobile);

public sealed record ContactAddress(
    [property: JsonPropertyName("addressId")] string AddressId,
    [property: JsonPropertyName("line1")] string Line1,
    [property: JsonPropertyName("line2")] string? Line2,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("postalCode")] string PostalCode,
    [property: JsonPropertyName("country")] string Country,
    [property: JsonPropertyName("isPrimary")] bool IsPrimary,
    [property: JsonPropertyName("label")] string? Label);

public sealed record ContactRole(
    [property: JsonPropertyName("roleId")] string RoleId,
    [property: JsonPropertyName("roleName")] string RoleName,
    [property: JsonPropertyName("roleRecordId")] string RoleRecordId,
    [property: JsonPropertyName("startedAt")] string StartedAt);

/// <summary>An owned calendar available to the active team on the local node.</summary>
public sealed record CalendarInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("colorToken")] string? ColorToken,
    [property: JsonPropertyName("isDefault")] bool IsDefault,
    [property: JsonPropertyName("resource")] string? Resource);

/// <summary>One concrete scheduled event occurrence returned by the node calendar agenda.</summary>
public sealed record ScheduledItem(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("recurrenceId")] string RecurrenceId,
    [property: JsonPropertyName("startUtc")] DateTimeOffset StartUtc,
    [property: JsonPropertyName("endUtc")] DateTimeOffset EndUtc,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("isOverride")] bool IsOverride);

/// <summary>A read-only invoice summary returned by the node invoice list.</summary>
public sealed record InvoiceSummary(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
    [property: JsonPropertyName("customerId")] string CustomerId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("issueDate")] DateTimeOffset IssueDate,
    [property: JsonPropertyName("dueDate")] DateTimeOffset DueDate,
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("taxTotal")] decimal TaxTotal,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("amountPaid")] decimal AmountPaid,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("propertyId")] string? PropertyId);

/// <summary>One line in a read-only invoice detail.</summary>
public sealed record InvoiceLine(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("lineNumber")] int LineNumber,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("incomeAccountId")] string IncomeAccountId,
    [property: JsonPropertyName("taxCodeId")] string? TaxCodeId,
    [property: JsonPropertyName("taxAmount")] decimal TaxAmount,
    [property: JsonPropertyName("propertyId")] string? PropertyId);

/// <summary>Full read-only invoice detail returned by the node.</summary>
public sealed record InvoiceDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("invoiceNumber")] string InvoiceNumber,
    [property: JsonPropertyName("customerId")] string CustomerId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("issueDate")] DateTimeOffset IssueDate,
    [property: JsonPropertyName("dueDate")] DateTimeOffset DueDate,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("subtotal")] decimal Subtotal,
    [property: JsonPropertyName("taxTotal")] decimal TaxTotal,
    [property: JsonPropertyName("total")] decimal Total,
    [property: JsonPropertyName("amountPaid")] decimal AmountPaid,
    [property: JsonPropertyName("balance")] decimal Balance,
    [property: JsonPropertyName("arAccountId")] string ArAccountId,
    [property: JsonPropertyName("subLedgerAccountId")] string? SubLedgerAccountId,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("voidedByEntryId")] string? VoidedByEntryId,
    [property: JsonPropertyName("writtenOffByEntryId")] string? WrittenOffByEntryId,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("externalRef")] string? ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<InvoiceLine> Lines);

/// <summary>The node's current chart resolution, used to scope invoice reads.</summary>
public sealed record ChartOfAccountsSnapshot(
    [property: JsonPropertyName("chartId")] string? ChartId);

/// <summary>Thin typed boundary for the in-process node's contacts surface.</summary>
public interface IHarborlineClient
{
    Task<SessionPermissionSnapshot> GetEffectivePermissionsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Contact>> ListContactsAsync(
        string? role = null,
        CancellationToken cancellationToken = default);

    Task<ContactDetail> GetContactAsync(
        string contactId,
        CancellationToken cancellationToken = default);

    Task<ContactDetail> CreateContactAsync(
        string displayName,
        string kind = "person",
        CancellationToken cancellationToken = default);

    Task<ContactRole> AddContactRoleAsync(
        string contactId,
        string roleName,
        CancellationToken cancellationToken = default);

    Task RemoveContactRoleAsync(
        string contactId,
        string roleId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScheduledItem>> ListUpcomingScheduledItemsAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    Task<ScheduledItem> GetScheduledItemAsync(
        string calendarId,
        string eventId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);

    Task<string> CreateScheduledItemAsync(
        string calendarId,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string timezone = "UTC",
        CancellationToken cancellationToken = default);

    Task<ChartOfAccountsSnapshot> GetChartOfAccountsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(
        string chartId,
        string? status = null,
        CancellationToken cancellationToken = default);

    Task<InvoiceDetail> GetInvoiceAsync(
        string invoiceId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleDefinitionDto>> ListRoleVocabularyAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuthorizationDefinitionDto>> ListAuthorizationDefinitionsAsync(
        CancellationToken cancellationToken = default);

    Task<AuthorizationBindingDto> GetAuthorizationBindingAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default);

    Task<NarrowAuthorizationBindingResponse> NarrowAuthorizationBindingAsync(
        Guid definitionId,
        NarrowAuthorizationBindingRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StandingDefinitionCatalogueDto>> ListStandingCatalogueAsync(
        CancellationToken cancellationToken = default);
}

#pragma warning restore CS1591

/// <summary>
/// Minimal .NET client for the node's existing HTTP surface. It intentionally has no direct
/// reference to node services or store types.
/// </summary>
public sealed class HarborlineClient : IHarborlineClient, IDisposable
{
    private const string ContactsPath = "/api/local-node/contacts";
    private const string CalendarsPath = "/api/local-node/calendar/calendars";
    private const string CalendarOccurrencesPath = "/api/local-node/calendar/occurrences";
    private const string SchedulingEventsPath = "/api/local-node/scheduling/events";
    private const string ChartOfAccountsPath = "/api/local-node/chart-of-accounts";
    private const string InvoicesPath = "/api/local-node/invoices";
    private const string EffectivePermissionsPath = "/api/session/permissions";
    private const string AuthorizationPath = "/api/local-node/authorization";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private bool _bootstrapped;

    /// <summary>Creates a client with its own HTTP transport.</summary>
    public HarborlineClient()
        : this(new HttpClient(), ownsHttp: true)
    {
    }

    /// <summary>Creates a client over an injected HTTP transport, primarily for host composition and tests.</summary>
    public HarborlineClient(HttpClient httpClient)
        : this(httpClient, ownsHttp: false)
    {
    }

    private HarborlineClient(HttpClient httpClient, bool ownsHttp)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttp = ownsHttp;
    }

    /// <summary>Bootstraps the loopback session and checks the node liveness route.</summary>
    public async Task<SessionBootstrap> BootstrapAsync(
        Uri nodeAddress,
        string sessionToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeAddress);
        if (string.IsNullOrWhiteSpace(sessionToken))
            throw new InvalidOperationException("The node session token is not available in memory.");

        _http.BaseAddress = nodeAddress.AbsoluteUri.EndsWith('/')
            ? nodeAddress
            : new Uri(nodeAddress.AbsoluteUri + "/", UriKind.Absolute);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", sessionToken);

        using var response = await _http.GetAsync("health", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _bootstrapped = true;
        return new SessionBootstrap(_http.BaseAddress);
    }

    /// <summary>Reads the node's current server-derived permission snapshot.</summary>
    public async Task<SessionPermissionSnapshot> GetEffectivePermissionsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync(EffectivePermissionsPath, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<EffectivePermissionsResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var permissions = payload?.EffectivePermissions ?? payload?.Permissions ?? Array.Empty<string>();
        return new SessionPermissionSnapshot(new HashSet<string>(permissions, StringComparer.Ordinal));
    }

    /// <summary>Lists contacts over the authenticated node session.</summary>
    public async Task<IReadOnlyList<Contact>> ListContactsAsync(
        string? role = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        var path = string.IsNullOrWhiteSpace(role)
            ? ContactsPath
            : $"{ContactsPath}?role={Uri.EscapeDataString(role)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<ContactListResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload?.Contacts ?? Array.Empty<Contact>();
    }

    /// <summary>Creates a contact over the authenticated node session.</summary>
    public async Task<ContactDetail> CreateContactAsync(
        string displayName,
        string kind = "person",
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.PostAsJsonAsync(
            ContactsPath,
            new { displayName, kind },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ContactDetail>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned an empty contact response.");
    }

    /// <summary>Reads a contact by its opaque node-issued identifier.</summary>
    public async Task<ContactDetail> GetContactAsync(string contactId, CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync($"{ContactsPath}/{Uri.EscapeDataString(contactId)}", cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ContactDetail>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned an empty contact response.");
    }

    /// <summary>Attaches a role to a contact over the authenticated node session.</summary>
    public async Task<ContactRole> AddContactRoleAsync(
        string contactId,
        string roleName,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.PostAsJsonAsync(
            $"{ContactsPath}/{Uri.EscapeDataString(contactId)}/roles",
            new { roleName },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ContactRole>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned an empty role response.");
    }

    /// <summary>Detaches a role from a contact over the authenticated node session.</summary>
    public async Task RemoveContactRoleAsync(
        string contactId,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.DeleteAsync(
            $"{ContactsPath}/{Uri.EscapeDataString(contactId)}/roles/{Uri.EscapeDataString(roleId)}",
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
    }

    /// <summary>Lists owned calendars for the active team.</summary>
    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync(CalendarsPath, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<CalendarListResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload?.Data ?? Array.Empty<CalendarInfo>();
    }

    /// <summary>Lists concrete upcoming occurrences from an owned calendar.</summary>
    public async Task<IReadOnlyList<ScheduledItem>> ListUpcomingScheduledItemsAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        var path = BuildCalendarOccurrencesPath(calendarId, fromUtc, toUtc);
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<OccurrenceListResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload?.Data ?? Array.Empty<ScheduledItem>();
    }

    /// <summary>Reads one scheduled occurrence using the node's agenda route and filters by opaque event id.</summary>
    public async Task<ScheduledItem> GetScheduledItemAsync(
        string calendarId,
        string eventId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var items = await ListUpcomingScheduledItemsAsync(calendarId, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);
        return items.FirstOrDefault(item => string.Equals(item.EventId, eventId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"The node returned no scheduled item for '{eventId}'.");
    }

    /// <summary>Creates a basic scheduled event on an owned calendar.</summary>
    public async Task<string> CreateScheduledItemAsync(
        string calendarId,
        string title,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string timezone = "UTC",
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.PostAsJsonAsync(
            SchedulingEventsPath,
            new
            {
                title,
                calendarId,
                timezone,
                startUtc,
                endUtc,
                allDay = false,
            },
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<CreatedScheduledItemResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload?.EventId
            ?? throw new InvalidOperationException("The node returned no scheduled item id.");
    }

    /// <summary>Gets the node's active chart id for invoice reads.</summary>
    public async Task<ChartOfAccountsSnapshot> GetChartOfAccountsAsync(CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync(ChartOfAccountsPath, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<ChartOfAccountsSnapshot>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned no chart-of-accounts response.");
    }

    /// <summary>Lists invoices in the node's active chart.</summary>
    public async Task<IReadOnlyList<InvoiceSummary>> ListInvoicesAsync(
        string chartId,
        string? status = null,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        var path = $"{InvoicesPath}?chartId={Uri.EscapeDataString(chartId)}";
        if (!string.IsNullOrWhiteSpace(status))
            path += $"&status={Uri.EscapeDataString(status)}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        var payload = await response.Content.ReadFromJsonAsync<InvoiceListResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return payload?.Data ?? Array.Empty<InvoiceSummary>();
    }

    /// <summary>Reads one invoice detail by its opaque node-issued identifier.</summary>
    public async Task<InvoiceDetail> GetInvoiceAsync(string invoiceId, CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync($"{InvoicesPath}/{Uri.EscapeDataString(invoiceId)}", cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<InvoiceDetailResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false) is { Data: var detail }
            ? detail
            : throw new InvalidOperationException("The node returned no invoice detail response.");
    }

    /// <summary>Lists the authoritative qualified role vocabulary.</summary>
    public async Task<IReadOnlyList<RoleDefinitionDto>> ListRoleVocabularyAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        return await GetArrayAsync<RoleDefinitionDto>($"{AuthorizationPath}/role-vocabulary", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lists every authorization definition with its active-tenant binding.</summary>
    public async Task<IReadOnlyList<AuthorizationDefinitionDto>> ListAuthorizationDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        return await GetArrayAsync<AuthorizationDefinitionDto>($"{AuthorizationPath}/capability-definitions", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads one active-tenant authorization binding.</summary>
    public async Task<AuthorizationBindingDto> GetAuthorizationBindingAsync(
        Guid definitionId,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        using var response = await _http.GetAsync(
            $"{AuthorizationPath}/capability-definitions/{definitionId:D}/binding", cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<AuthorizationBindingDto>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned an empty authorization binding response.");
    }

    /// <summary>Appends one idempotent narrow-only authorization binding revision.</summary>
    public async Task<NarrowAuthorizationBindingResponse> NarrowAuthorizationBindingAsync(
        Guid definitionId,
        NarrowAuthorizationBindingRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{AuthorizationPath}/capability-definitions/{definitionId:D}/binding")
        {
            Content = JsonContent.Create(request),
        };
        message.Headers.Add("Idempotency-Key", idempotencyKey);
        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<NarrowAuthorizationBindingResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The node returned an empty authorization binding response.");
    }

    /// <summary>Lists standing rules and the record types carrying each declared field.</summary>
    public async Task<IReadOnlyList<StandingDefinitionCatalogueDto>> ListStandingCatalogueAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBootstrapped();
        return await GetArrayAsync<StandingDefinitionCatalogueDto>($"{AuthorizationPath}/standing-catalogue", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private async Task<IReadOnlyList<T>> GetArrayAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response);
        return await response.Content.ReadFromJsonAsync<T[]>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<T>();
    }

    private void EnsureBootstrapped()
    {
        if (!_bootstrapped)
            throw new InvalidOperationException("BootstrapAsync must complete before using the client.");
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new HarborlinePermissionDeniedException();
        }

        response.EnsureSuccessStatusCode();
    }

    private sealed record ContactListResponse(
        [property: JsonPropertyName("contacts")] Contact[] Contacts);

    private sealed record CalendarListResponse(
        [property: JsonPropertyName("data")] CalendarInfo[] Data);

    private sealed record OccurrenceListResponse(
        [property: JsonPropertyName("data")] ScheduledItem[] Data);

    private sealed record CreatedScheduledItemResponse(
        [property: JsonPropertyName("eventId")] string EventId);

    private sealed record InvoiceListResponse(
        [property: JsonPropertyName("data")] InvoiceSummary[] Data);

    private sealed record InvoiceDetailResponse(
        [property: JsonPropertyName("data")] InvoiceDetail Data);

    private sealed record EffectivePermissionsResponse(
        [property: JsonPropertyName("permissions")] string[]? Permissions,
        [property: JsonPropertyName("effectivePermissions")] string[]? EffectivePermissions);

    private static string BuildCalendarOccurrencesPath(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc) =>
        $"{CalendarOccurrencesPath}?calendarId={Uri.EscapeDataString(calendarId)}" +
        $"&fromUtc={Uri.EscapeDataString(fromUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}" +
        $"&toUtc={Uri.EscapeDataString(toUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))}";
}
