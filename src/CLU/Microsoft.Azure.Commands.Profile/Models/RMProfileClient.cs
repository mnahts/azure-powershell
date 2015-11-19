﻿// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Microsoft.Azure.Common.Authentication;
using Microsoft.Azure.Common.Authentication.Factories;
using Microsoft.Azure.Common.Authentication.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Security;
using Microsoft.Azure.Commands.Profile.Models;
using Microsoft.Azure.Commands.Profile;
using Microsoft.Azure.Commands.Profile.Properties;
using Microsoft.Azure.Management.Resources;
using Microsoft.Azure.Management.Resources.Models;
using Microsoft.Rest.Azure;
using Microsoft.Rest;

namespace Microsoft.Azure.Commands.ResourceManager.Common
{
    public class RMProfileClient
    {
        private IAuthenticationFactory _authenticationFactory;
        private IClientFactory _clientFactory;
        private AzureRMProfile _profile;
        public Action<string> WarningLog;

        public RMProfileClient(IAuthenticationFactory authenticationFactory, IClientFactory clientFactory, AzureRMProfile profile)
        {
            _profile = profile;
            _authenticationFactory = authenticationFactory;
            _clientFactory = clientFactory;

            if (_profile != null && _profile.Context != null &&
                _profile.Context.TokenCache != null && _profile.Context.TokenCache.Length > 0)
            {
                TokenCache.DefaultShared.Deserialize(_profile.Context.TokenCache);
            }
        }

        public AzureRMProfile Login(
            AzureAccount account, 
            AzureEnvironment environment, 
            string tenantId, 
            string subscriptionId, 
            string subscriptionName, 
            string password)
        {
            AzureSubscription newSubscription = null;
            AzureTenant newTenant = null;
            ShowDialog promptBehavior = 
                (password == null && 
                 account.Type != AzureAccount.AccountType.AccessToken && 
                 !account.IsPropertySet(AzureAccount.Property.CertificateThumbprint))
                ? ShowDialog.Always : ShowDialog.Never;

            // (tenant and subscription are present) OR
            // (tenant is present and subscription is not provided)
            if (!string.IsNullOrEmpty(tenantId))
            {
                var token = AcquireAccessToken(account, environment, tenantId, password, promptBehavior);
                TryGetTenantSubscription(token, account, environment, tenantId, subscriptionId, subscriptionName, out newSubscription, out newTenant);
            }
            // (tenant is not provided and subscription is present) OR
            // (tenant is not provided and subscription is not provided)
            else
            {
                foreach (var tenant in ListAccountTenants(account, environment, password, promptBehavior))
                {
                    AzureTenant tempTenant;
                    AzureSubscription tempSubscription;
                    var token = AcquireAccessToken(account, environment, tenant.Id.ToString(), password,
                        ShowDialog.Auto);
                    if (newTenant == null && TryGetTenantSubscription(token, account, environment, tenant.Id.ToString(), subscriptionId, subscriptionName, out tempSubscription, out tempTenant) &&
                        newTenant == null)
                    {
                        newTenant = tempTenant;
                        newSubscription = tempSubscription;
                    }
                }
            }

            if (newSubscription == null)
            {
                if (subscriptionId != null)
                {
                    throw new PSInvalidOperationException(String.Format(Resources.SubscriptionIdNotFound, account.Id, subscriptionId));
                }
                else if (subscriptionName != null)
                {
                    throw new PSInvalidOperationException(String.Format(Resources.SubscriptionNameNotFound, account.Id, subscriptionName));
                }

                _profile.Context = new AzureContext(account, environment, newTenant);
            }
            else
            {
                _profile.Context = new AzureContext(newSubscription, account, environment, newTenant);
            }
            
            _profile.Context.TokenCache = TokenCache.DefaultShared.Serialize();

            return _profile;
        }

        public AzureContext SetCurrentContext(string tenantId)
        {
            AzureSubscription firstSubscription = GetFirstSubscription(tenantId);

            if (firstSubscription != null)
            {
                SwitchSubscription(firstSubscription);
            }
            else
            {
                if (_profile.Context.Account != null)
                {
                    _profile.Context.Account.Properties[AzureAccount.Property.Tenants] = tenantId;
                }
                //TODO: should not we clean up this field? It could be a bogus subscription we are leaving behind...
                if (_profile.Context.Subscription != null)
                {
                    _profile.Context.Subscription.Properties[AzureSubscription.Property.Tenants] = tenantId;
                }
                _profile.SetContextWithCache(new AzureContext(
                     _profile.Context.Account,
                     _profile.Context.Environment,
                     CreateTenant(tenantId)));            
            }
            return _profile.Context;
        }

        public AzureContext SetCurrentContext(string subscriptionId, string subscriptionName, string tenantId)
        {
            AzureSubscription subscription;

            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                TryGetSubscriptionById(tenantId, subscriptionId, out subscription);
            }
            else if (!string.IsNullOrWhiteSpace(subscriptionName))
            {
                TryGetSubscriptionByName(tenantId, subscriptionName, out subscription);
            }
            else
            {
                throw new ArgumentException(string.Format(
                    "Please provide either subscriptionId or subscriptionName"));
            }

            if (subscription == null)
            {
                string subscriptionFilter = string.IsNullOrWhiteSpace(subscriptionId) ? subscriptionName : subscriptionId;
                throw new ArgumentException(string.Format(
                    "Provided subscription {0} does not exist", subscriptionFilter));
            }
            else
            {
                SwitchSubscription(subscription);
            }

            return _profile.Context;
        }

        private void SwitchSubscription(AzureSubscription subscription)
        {
            string tenantId = subscription.Properties[AzureSubscription.Property.Tenants];

            if (_profile.Context.Account != null)
            {
                _profile.Context.Account.Properties[AzureAccount.Property.Tenants] = tenantId;
            }
            if (_profile.Context.Subscription != null)
            {
                _profile.Context.Subscription.Properties[AzureSubscription.Property.Tenants] = tenantId;
            }

            var newSubscription = new AzureSubscription { Id = subscription.Id };
            if (_profile.Context.Subscription != null)
            {
                newSubscription.Account = _profile.Context.Subscription.Account;
                newSubscription.Environment = _profile.Context.Subscription.Environment;
                newSubscription.Properties = _profile.Context.Subscription.Properties;
                newSubscription.Name = (subscription == null) ? null : subscription.Name;
            }

            _profile.SetContextWithCache(new AzureContext(
                newSubscription,
                _profile.Context.Account,
                _profile.Context.Environment,
                CreateTenant(tenantId)));
        }

        public List<AzureTenant> ListTenants(string tenant)
        {
            return ListAccountTenants(_profile.Context.Account, _profile.Context.Environment, null, ShowDialog.Auto)
                .Where(t => tenant == null ||
                            tenant.Equals(t.Id.ToString(), StringComparison.OrdinalIgnoreCase) ||
                            tenant.Equals(t.Domain, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public bool TryGetSubscriptionById(string tenantId, string subscriptionId, out AzureSubscription subscription)
        {
            Guid subscriptionIdGuid;
            subscription = null;
            if (Guid.TryParse(subscriptionId, out subscriptionIdGuid)) 
            { 
                IEnumerable<AzureSubscription> subscriptionList = GetSubscriptions(tenantId);
                subscription = subscriptionList.FirstOrDefault(s => s.Id == subscriptionIdGuid);
            }
            return subscription != null;
        }

        public bool TryGetSubscriptionByName(string tenantId, string subscriptionName, out AzureSubscription subscription)
        {
            IEnumerable<AzureSubscription> subscriptionList = GetSubscriptions(tenantId);
            subscription = subscriptionList.FirstOrDefault(s => s.Name.Equals(subscriptionName, StringComparison.OrdinalIgnoreCase));

            return subscription != null;
        }

        private AzureSubscription GetFirstSubscription(string tenantId)
        {
            IEnumerable<AzureSubscription> subscriptionList = GetSubscriptions(null);
            return subscriptionList.FirstOrDefault();
        }

        public IEnumerable<AzureSubscription> GetSubscriptions(string tenantId)
        {
            IEnumerable<AzureSubscription> subscriptionList= new List<AzureSubscription>();
            if (string.IsNullOrWhiteSpace(tenantId))
            {
                subscriptionList = ListSubscriptions();
            }
            else
            {
                subscriptionList = ListSubscriptions(tenantId);
            }

            return subscriptionList;
        }

        public AzureEnvironment AddOrSetEnvironment(AzureEnvironment environment)
        {
            if (environment == null)
            {
                throw new ArgumentNullException("environment", Resources.EnvironmentNeedsToBeSpecified);
            }

            if (AzureEnvironment.PublicEnvironments.ContainsKey(environment.Name))
            {
                throw new InvalidOperationException(
                    string.Format(Resources.ChangingDefaultEnvironmentNotSupported, "environment"));
            }

            if (_profile.Environments.ContainsKey(environment.Name))
            {
                _profile.Environments[environment.Name] =
                    MergeEnvironmentProperties(environment, _profile.Environments[environment.Name]);
            }
            else
            {
                _profile.Environments[environment.Name] = environment;
            }

            return _profile.Environments[environment.Name];
        }

        public List<AzureEnvironment> ListEnvironments(string name)
        {
            var result = new List<AzureEnvironment>();

            if (string.IsNullOrWhiteSpace(name))
            {
                result.AddRange(_profile.Environments.Values);
            }
            else if (_profile.Environments.ContainsKey(name))
            {
                result.Add(_profile.Environments[name]);
            }

            return result;
        }

        public AzureEnvironment RemoveEnvironment(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException("name", Resources.EnvironmentNameNeedsToBeSpecified);
            }
            if (AzureEnvironment.PublicEnvironments.ContainsKey(name))
            {
                throw new ArgumentException(Resources.RemovingDefaultEnvironmentsNotSupported, "name");
            }

            if (_profile.Environments.ContainsKey(name))
            {
                var environment = _profile.Environments[name];
                _profile.Environments.Remove(name);
                return environment;
            }
            else
            {
                throw new ArgumentException(string.Format(Resources.EnvironmentNotFound, name), "name");
            }
        }

        public IAccessToken AcureAccessToken(string tenantId)
        {
            return AcquireAccessToken(_profile.Context.Account, _profile.Context.Environment, tenantId, null, ShowDialog.Auto);
        }

        /// <summary>
        /// List all tenants for the account in the profile context
        /// </summary>
        /// <returns>The list of tenants for the default account.</returns>
        public IEnumerable<AzureTenant> ListTenants()
        {
            return ListAccountTenants(_profile.Context.Account, _profile.Context.Environment, null, ShowDialog.Never);
        }

        public IEnumerable<AzureSubscription> ListSubscriptions(string tenant)
        {
            return ListSubscriptionsForTenant(_profile.Context.Account, _profile.Context.Environment, null,
                ShowDialog.Never, tenant);
        }

        public IEnumerable<AzureSubscription> ListSubscriptions()
        {
            List<AzureSubscription> subscriptions = new List<AzureSubscription>();
            foreach (var tenant in ListTenants())
            {
                try
                {
                    subscriptions.AddRange(ListSubscriptions(tenant.Id.ToString()));
                }
                catch (AadAuthenticationException)
                {
                    WriteWarningMessage(string.Format("Could not authenticate user account {0} with tenant {1}.  " +
                       "Subscriptions in this tenant will not be listed. Please login again using Login-AzureRmAccount " +
                       "to view the subscriptions in this tenant.", _profile.Context.Account, tenant));
                }

            }

            return subscriptions;
        }

        private AzureTenant CreateTenant(string tenantIdOrDomain)
        {
            var tenant = new AzureTenant();
            Guid tenantIdGuid;
            if (Guid.TryParse(tenantIdOrDomain, out tenantIdGuid))
            {
                tenant.Id = tenantIdGuid;
            }
            else
            {
                tenant.Domain = tenantIdOrDomain;
            }
            return tenant;
        }

        private AzureEnvironment MergeEnvironmentProperties(AzureEnvironment environment1, AzureEnvironment environment2)
        {
            if (environment1 == null || environment2 == null)
            {
                throw new ArgumentNullException("environment1");
            }
            if (!string.Equals(environment1.Name, environment2.Name, StringComparison.CurrentCultureIgnoreCase))
            {
                throw new ArgumentException("Environment names do not match.");
            }
            AzureEnvironment mergedEnvironment = new AzureEnvironment
            {
                Name = environment1.Name
            };

            // Merge all properties
            foreach (AzureEnvironment.Endpoint property in Enum.GetValues(typeof(AzureEnvironment.Endpoint)))
            {
                string propertyValue = environment1.GetEndpoint(property) ?? environment2.GetEndpoint(property);
                if (propertyValue != null)
                {
                    mergedEnvironment.Endpoints[property] = propertyValue;
                }
            }

            return mergedEnvironment;
        }

        private IAccessToken AcquireAccessToken(AzureAccount account,
            AzureEnvironment environment,
            string tenantId,
            string password,
            ShowDialog promptBehavior)
        {
            if (account.Type == AzureAccount.AccountType.AccessToken)
            {
                tenantId = tenantId ?? "Common";
                return new SimpleAccessToken(account, tenantId);
            }

            return _authenticationFactory.Authenticate(
                account,
                environment,
                tenantId,
                password,
                TokenCache.DefaultShared);
        }

        private bool TryGetTenantSubscription(IAccessToken accessToken,
            AzureAccount account,
            AzureEnvironment environment,
            string tenantId,
            string subscriptionId,
            string subscriptionName,
            out AzureSubscription subscription,
            out AzureTenant tenant)
        {
            using (var subscriptionClient = _clientFactory.CreateCustomArmClient<SubscriptionClient>(
                environment.GetEndpointAsUri(AzureEnvironment.Endpoint.ResourceManager), 
                new TokenCredentials(accessToken.AccessToken)))
            {
                //TODO: Fix subscription client to not require subscriptionId
                subscriptionClient.SubscriptionId = subscriptionId ?? Guid.NewGuid().ToString();
                Subscription subscriptionFromServer = null;

                try
                {
                    if (subscriptionId != null)
                    {
                        subscriptionFromServer = subscriptionClient.Subscriptions.Get(subscriptionId);
                    }
                    else
                    {
                        var subscriptions = subscriptionClient.Subscriptions.List();
                        if (subscriptions != null && subscriptions.Any())
                        {
                            if (subscriptionName != null)
                            {
                                subscriptionFromServer = subscriptions.FirstOrDefault(s => s.DisplayName.Equals(subscriptionName, StringComparison.OrdinalIgnoreCase));
                            }
                            else
                            {
                                if (subscriptions.Any())
                                {
                                    WriteWarningMessage(string.Format(
                                        "TenantId '{0}' contains more than one subscription. First one will be selected for further use. " +
                                        "To select another subscription, use Set-AzureRmContext.",
                                        tenantId));
                                }
                                subscriptionFromServer = subscriptions.First();
                            }
                        }
                    }
                }
                catch (CloudException ex)
                {
                    WriteWarningMessage(ex.Message);
                }

                if (subscriptionFromServer != null)
                {
                    subscription = new AzureSubscription
                    {
                        Id = new Guid(subscriptionFromServer.SubscriptionId),
                        Account = accessToken.UserId,
                        Environment = environment.Name,
                        Name = subscriptionFromServer.DisplayName,
                        Properties = new Dictionary<AzureSubscription.Property, string> { { AzureSubscription.Property.Tenants, accessToken.TenantId } }
                    };

                    account.Properties[AzureAccount.Property.Tenants] = accessToken.TenantId;
                    tenant = new AzureTenant();
                    tenant.Id = new Guid(accessToken.TenantId);
                    tenant.Domain = accessToken.GetDomain();
                    return true;
                }

                subscription = null;

                if (accessToken != null && accessToken.TenantId != null)
                {
                    tenant = new AzureTenant();
                    tenant.Id = Guid.Parse(accessToken.TenantId);
                    if (accessToken.UserId != null)
                    {
                        var domain = accessToken.UserId.Split(new[] { '@' }, StringSplitOptions.RemoveEmptyEntries);
                        if (domain.Length == 2)
                        {
                            tenant.Domain = domain[1];
                        }
                    }
                    return true;
                }

                tenant = null;
                return false;
            }
        }

        private List<AzureTenant> ListAccountTenants(AzureAccount account, AzureEnvironment environment, string password, ShowDialog promptBehavior)
        {
            var commonTenantToken = AcquireAccessToken(account, environment, AuthenticationFactory.CommonAdTenant,
                password, promptBehavior);

            using (var subscriptionClient = _clientFactory.CreateCustomArmClient<SubscriptionClient>(
                    environment.GetEndpointAsUri(AzureEnvironment.Endpoint.ResourceManager), 
                    new TokenCredentials(commonTenantToken.AccessToken)
                    ))
            {
                //TODO: Fix subscription client to not require subscriptionId
                subscriptionClient.SubscriptionId = Guid.NewGuid().ToString();
                return subscriptionClient.Tenants.List()
                    .Select(ti => new AzureTenant() { Id = new Guid(ti.TenantId), Domain = commonTenantToken.GetDomain() })
                    .ToList();
            }
        }

        private IEnumerable<AzureSubscription> ListSubscriptionsForTenant(AzureAccount account, AzureEnvironment environment,
            string password, ShowDialog promptBehavior, string tenantId)
        {
            var accessToken = AcquireAccessToken(account, environment, tenantId, password, promptBehavior);
            using (var subscriptionClient = _clientFactory.CreateCustomArmClient<SubscriptionClient>(
                environment.GetEndpointAsUri(AzureEnvironment.Endpoint.ResourceManager), 
                new TokenCredentials(accessToken.AccessToken)
                ))
            {
                //TODO: Fix subscription client to not require subscriptionId
                subscriptionClient.SubscriptionId = Guid.NewGuid().ToString();
                var subscriptions = subscriptionClient.Subscriptions.List();
                if (subscriptions != null && subscriptions.Any())
                {
                    return
                        subscriptions.Select(
                            (s) =>
                                s.ToAzureSubscription(new AzureContext(_profile.Context.Subscription, account,
                                    environment, CreateTenantFromString(tenantId))));
                }

                return new List<AzureSubscription>();
            }
        }

        private void WriteWarningMessage(string message)
        {
            if (WarningLog != null)
            {
                WarningLog(message);
            }
        }

        private static AzureTenant CreateTenantFromString(string tenantOrDomain)
        {
            AzureTenant result = new AzureTenant();
            Guid id;
            if (Guid.TryParse(tenantOrDomain, out id))
            {
                result.Id = id;
            }
            else
            {
                result.Domain = tenantOrDomain;
            }

            return result;
        }
    }
}
