using Amazon;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.Options;
using System.Security.Cryptography.X509Certificates;
using UnifiedGateway.Models;

namespace UnifiedGateway.Services;

public class STSService : ISTSService, IDisposable
{
    private readonly GatewayOptions _options;
    private readonly ISecurityService _securityService;
    private readonly ILogger<STSService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private AWSCredentials? _cachedCredentials;
    private DateTimeOffset? _expirationUtc;
    private string? _lastError;
    private bool _isAssumedRole;
    private AwsAuthenticationType _activeAuthType;

    public STSService(
        IOptions<GatewayOptions> options,
        ISecurityService securityService,
        ILogger<STSService> logger)
    {
        _options = options.Value;
        _securityService = securityService;
        _logger = logger;
        _activeAuthType = _options.Aws.ResolvedAuthType;
    }

    public async Task<AWSCredentials> GetCredentialsAsync(CancellationToken cancellationToken = default)
    {
        if (ShouldRefresh())
        {
            await RefreshCredentialsAsync(cancellationToken);
        }

        if (_cachedCredentials == null)
        {
            throw new InvalidOperationException($"AWS Credentials are not available. Last error: {_lastError ?? "No credentials found in environment or AWS profile."}");
        }

        return _cachedCredentials;
    }

    public async Task<AwsCredentialStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var bufferMinutes = _options.Aws.RefreshBufferMinutes;
            var isExpiring = _expirationUtc.HasValue &&
                             _expirationUtc.Value <= DateTimeOffset.UtcNow.AddMinutes(bufferMinutes);

            var rolesAnywhereOpts = _options.Aws.RolesAnywhere;
            var currentAuthType = _activeAuthType != AwsAuthenticationType.Direct ? _activeAuthType : _options.Aws.ResolvedAuthType;

            return new AwsCredentialStatus
            {
                IsInitialized = _cachedCredentials != null,
                IsAssumedRole = _isAssumedRole,
                AuthenticationType = currentAuthType.ToString(),
                Region = _options.Aws.Region,
                RoleArnMasked = !string.IsNullOrEmpty(_options.Aws.AssumeRoleArn)
                    ? _securityService.MaskSecret(_options.Aws.AssumeRoleArn, 12)
                    : !string.IsNullOrEmpty(rolesAnywhereOpts?.RoleArn)
                        ? _securityService.MaskSecret(rolesAnywhereOpts.RoleArn, 12)
                        : null,
                ProfileUsed = currentAuthType == AwsAuthenticationType.Profile && _cachedCredentials != null ? _options.Aws.LocalProfileName : null,
                RolesAnywhereProfileMasked = !string.IsNullOrEmpty(rolesAnywhereOpts?.ProfileArn)
                    ? _securityService.MaskSecret(rolesAnywhereOpts.ProfileArn, 12)
                    : null,
                TrustAnchorMasked = !string.IsNullOrEmpty(rolesAnywhereOpts?.TrustAnchorArn)
                    ? _securityService.MaskSecret(rolesAnywhereOpts.TrustAnchorArn, 12)
                    : null,
                ExpirationUtc = _expirationUtc,
                IsExpiringSoon = isExpiring,
                LastError = _lastError
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RefreshCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var authType = _options.Aws.ResolvedAuthType;
            _activeAuthType = authType;

            _logger.LogInformation("Verifying AWS credentials. Region={Region}, AuthType={AuthType}",
                _options.Aws.Region, authType);

            var regionEndpoint = RegionEndpoint.GetBySystemName(_options.Aws.Region);
            AWSCredentials? baseCredentials = null;

            switch (authType)
            {
                case AwsAuthenticationType.RolesAnywhere:
                    baseCredentials = await ResolveRolesAnywhereCredentialsAsync(regionEndpoint, cancellationToken);
                    break;

                case AwsAuthenticationType.Profile:
                    baseCredentials = ResolveProfileCredentials();
                    break;

                case AwsAuthenticationType.AssumeRole:
                case AwsAuthenticationType.Direct:
                default:
                    var directCreds = FallbackCredentialsFactory.GetCredentials();
                    if (CanResolveValidKeys(directCreds))
                    {
                        baseCredentials = directCreds;
                    }
                    else
                    {
                        _lastError = "No AWS credentials found in environment, EC2 metadata, or ~/.aws/credentials.";
                    }
                    break;
            }

            if (baseCredentials == null)
            {
                _cachedCredentials = null;
                _expirationUtc = null;
                _isAssumedRole = false;
                _logger.LogInformation("AWS credentials not present on this host. Status: Offline / Not Configured ({Reason})", _lastError);
                return;
            }

            // If an AssumeRoleArn is configured and auth type is AssumeRole, perform STS AssumeRole
            if (authType == AwsAuthenticationType.AssumeRole && !string.IsNullOrWhiteSpace(_options.Aws.AssumeRoleArn))
            {
                _logger.LogInformation("Assuming AWS IAM Role: {RoleArnMasked}",
                    _securityService.MaskSecret(_options.Aws.AssumeRoleArn, 12));

                using var stsClient = new AmazonSecurityTokenServiceClient(baseCredentials, regionEndpoint);
                var assumeRequest = new AssumeRoleRequest
                {
                    RoleArn = _options.Aws.AssumeRoleArn,
                    RoleSessionName = _options.Aws.RoleSessionName,
                    DurationSeconds = _options.Aws.SessionDurationSeconds
                };

                if (!string.IsNullOrWhiteSpace(_options.Aws.ExternalId))
                {
                    assumeRequest.ExternalId = _options.Aws.ExternalId;
                }

                var response = await stsClient.AssumeRoleAsync(assumeRequest, cancellationToken);
                var creds = response.Credentials;

                _cachedCredentials = new SessionAWSCredentials(
                    creds.AccessKeyId,
                    creds.SecretAccessKey,
                    creds.SessionToken
                );

                _expirationUtc = new DateTimeOffset(creds.Expiration);
                _isAssumedRole = true;
                _lastError = null;

                _logger.LogInformation("STS AssumeRole succeeded. Session valid until: {ExpirationUtc}", _expirationUtc);
            }
            else if (authType != AwsAuthenticationType.RolesAnywhere)
            {
                _cachedCredentials = baseCredentials;
                _expirationUtc = null;
                _isAssumedRole = false;
                _lastError = null;

                _logger.LogInformation("Using verified AWS credentials ({AuthType}).", authType);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _cachedCredentials = null;
            _logger.LogWarning(ex, "AWS credentials verification encountered an issue: {Message}", ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    private AWSCredentials? ResolveProfileCredentials()
    {
        var profileName = string.IsNullOrWhiteSpace(_options.Aws.LocalProfileName)
            ? "default"
            : _options.Aws.LocalProfileName;

        _logger.LogInformation("Checking for local AWS profile: '{ProfileName}'", profileName);
        var chain = new CredentialProfileStoreChain();
        if (chain.TryGetAWSCredentials(profileName, out var profileCreds) && CanResolveValidKeys(profileCreds))
        {
            _logger.LogInformation("Successfully verified AWS profile '{ProfileName}'.", profileName);
            return profileCreds;
        }

        // Check if environment variables exist
        var fallback = FallbackCredentialsFactory.GetCredentials();
        if (CanResolveValidKeys(fallback))
        {
            return fallback;
        }

        _lastError = $"AWS CLI profile '{profileName}' was not found in ~/.aws/config or ~/.aws/credentials.";
        return null;
    }

    private Task<AWSCredentials?> ResolveRolesAnywhereCredentialsAsync(RegionEndpoint regionEndpoint, CancellationToken cancellationToken)
    {
        var rolesAnywhereOpts = _options.Aws.RolesAnywhere;
        _logger.LogInformation("Checking AWS IAM Roles Anywhere configuration. TrustAnchor={TrustAnchor}, Profile={Profile}",
            _securityService.MaskSecret(rolesAnywhereOpts.TrustAnchorArn, 12),
            _securityService.MaskSecret(rolesAnywhereOpts.ProfileArn, 12));

        // 1. Check if configured X.509 certificate file is present
        if (!string.IsNullOrWhiteSpace(rolesAnywhereOpts.CertificatePath) && File.Exists(rolesAnywhereOpts.CertificatePath))
        {
            try
            {
                var cert = new X509Certificate2(rolesAnywhereOpts.CertificatePath);
                _logger.LogInformation("Loaded X.509 client certificate for Roles Anywhere: Subject={Subject}, ValidTo={ValidTo}",
                    cert.Subject, cert.NotAfter);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to parse X.509 certificate at {Path}", rolesAnywhereOpts.CertificatePath);
            }
        }

        // 2. Check if a local AWS profile configured for rolesanywhere exists
        var chain = new CredentialProfileStoreChain();
        if (chain.TryGetAWSCredentials("rolesanywhere", out var raCreds) && CanResolveValidKeys(raCreds))
        {
            _cachedCredentials = raCreds;
            _expirationUtc = DateTimeOffset.UtcNow.AddSeconds(_options.Aws.SessionDurationSeconds);
            _isAssumedRole = true;
            _lastError = null;
            return Task.FromResult<AWSCredentials?>(raCreds);
        }

        // 3. Fall back to standard credential chain if valid keys exist
        var fallback = FallbackCredentialsFactory.GetCredentials();
        if (CanResolveValidKeys(fallback))
        {
            _cachedCredentials = fallback;
            _expirationUtc = DateTimeOffset.UtcNow.AddSeconds(_options.Aws.SessionDurationSeconds);
            _isAssumedRole = true;
            _lastError = null;
            return Task.FromResult<AWSCredentials?>(fallback);
        }

        _lastError = "AWS IAM Roles Anywhere certificate or signing profile not configured on this host.";
        return Task.FromResult<AWSCredentials?>(null);
    }

    private static bool CanResolveValidKeys(AWSCredentials? creds)
    {
        if (creds == null) return false;
        try
        {
            var imm = creds.GetCredentials();
            return imm != null && !string.IsNullOrWhiteSpace(imm.AccessKey);
        }
        catch
        {
            return false;
        }
    }

    private bool ShouldRefresh()
    {
        if (_cachedCredentials == null)
            return true;

        if (!_expirationUtc.HasValue)
            return false;

        var bufferMinutes = _options.Aws.RefreshBufferMinutes;
        return DateTimeOffset.UtcNow.AddMinutes(bufferMinutes) >= _expirationUtc.Value;
    }

    public void Dispose()
    {
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
