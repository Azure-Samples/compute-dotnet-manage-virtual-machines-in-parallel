// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Compute;
using Azure.ResourceManager.Compute.Models;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Storage;
using Azure.ResourceManager.Storage.Models;
using Azure.ResourceManager.TrafficManager;

namespace ManageVirtualMachinesInParallel
{
    public class Program
    {
        private const int vmCount = 2;
        private static readonly string userName = Utilities.CreateUsername();
        private static readonly string password = Utilities.CreatePassword();

        /**
         * Azure Compute sample for managing virtual machines -
         *  - Create N virtual machines in parallel
         */
        public static void RunSample(ArmClient client)
        {
            var region = AzureLocation.EastUS;
            string rgName = Utilities.CreateRandomName("rgCOPP");
            string networkName = Utilities.CreateRandomName("vnetCOMV");
            string storageAccountName = Utilities.CreateRandomName("stgCOMV");
            string pipName = Utilities.CreateRandomName("pip1");
            var storageAccountSkuName = Utilities.CreateRandomName("stasku");
            var pipDnsLabelLinuxVM = Utilities.CreateRandomName("rgpip1");
            var trafficManagerName = Utilities.CreateRandomName("tra");
            var lro = client.GetDefaultSubscription().GetResourceGroups().CreateOrUpdate(Azure.WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
            var resourceGroup = lro.Value;

            try
            {
                // Prepare Creatable Network definition [Where all the virtual machines get added to]
                var networkCollection = resourceGroup.GetVirtualNetworks();
                var networkData = new VirtualNetworkData()
                {
                    Location = region,
                    AddressPrefixes =
                    {
                        "172.16.0.0/16"
                    }
                };
                var networkCreatable = networkCollection.CreateOrUpdate(Azure.WaitUntil.Completed, networkName, networkData).Value;

                // Prepare Creatable Storage account definition [For storing VMs disk]
                var storageAccountCollection = resourceGroup.GetStorageAccounts();
                var storageAccountData = new StorageAccountCreateOrUpdateContent(new StorageSku(storageAccountSkuName), StorageKind.Storage, region);
                {
                };
                var storageAccountCreatable = storageAccountCollection.CreateOrUpdate(WaitUntil.Completed, storageAccountName, storageAccountData).Value;

                // Create 1 public IP address creatable
                var publicIpAddressCollection = resourceGroup.GetPublicIPAddresses();
                var publicIPAddressData = new PublicIPAddressData()
                {
                    Location = region,
                    DnsSettings =
                            {
                                DomainNameLabel = pipDnsLabelLinuxVM
                            }
                };
                var publicIpAddressCreatable = (publicIpAddressCollection.CreateOrUpdate(Azure.WaitUntil.Completed, pipName, publicIPAddressData)).Value;
                //Create a subnet
                Utilities.Log("Creating a Linux subnet...");
                var subnetName = Utilities.CreateRandomName("subnet_");
                var subnetData = new SubnetData()
                {
                    ServiceEndpoints =
                    {
                        new ServiceEndpointProperties()
                        {
                            Service = "Microsoft.Storage"
                        }
                    },
                    Name = subnetName,
                    AddressPrefix = "10.0.0.0/28",
                };
                var subnetLRro = networkCreatable.GetSubnets().CreateOrUpdate(WaitUntil.Completed, subnetName, subnetData);
                var subnet = subnetLRro.Value;
                Utilities.Log("Created a Linux subnet with name : " + subnet.Data.Name);

                //Create a networkInterface
                Utilities.Log("Created a linux networkInterface");
                var networkInterfaceData = new NetworkInterfaceData()
                {
                    Location = region,
                    IPConfigurations =
                    {
                        new NetworkInterfaceIPConfigurationData()
                        {
                            Name = "internal",
                            Primary = true,
                            Subnet = new SubnetData
                            {
                                Name = subnetName,
                                Id = new ResourceIdentifier($"{networkCreatable.Data.Id}/subnets/{subnetName}")
                            },
                            PrivateIPAllocationMethod = NetworkIPAllocationMethod.Dynamic,
                            PublicIPAddress = publicIpAddressCreatable.Data,
                        }
                    }
                };
                var networkInterfaceName = Utilities.CreateRandomName("networkInterface");
                var nic = (resourceGroup.GetNetworkInterfaces().CreateOrUpdate(WaitUntil.Completed, networkInterfaceName, networkInterfaceData)).Value;
                Utilities.Log("Created a Linux networkInterface with name : " + nic.Data.Name);
                var virtualMachineCollection = resourceGroup.GetVirtualMachines();
                var linuxComputerName = Utilities.CreateRandomName("linuxComputer");
                // Prepare a batch of Creatable Virtual Machines definitions
                List<VirtualMachineResource> creatableVirtualMachines = new List<VirtualMachineResource>();

                for (int i = 0; i < vmCount; i++)
                {
                    var linuxVmdata = new VirtualMachineData(region)
                    {
                        HardwareProfile = new VirtualMachineHardwareProfile()
                        {
                            VmSize = "Standard_D2a_v4"
                        },
                        OSProfile = new VirtualMachineOSProfile()
                        {
                            AdminUsername = userName,
                            AdminPassword = password,
                            ComputerName = linuxComputerName,
                        },
                        NetworkProfile = new VirtualMachineNetworkProfile()
                        {
                            NetworkInterfaces =
                        {
                            new VirtualMachineNetworkInterfaceReference()
                            {
                                Id = nic.Id,
                                Primary = true,
                            }
                        }
                        },
                        StorageProfile = new VirtualMachineStorageProfile()
                        {
                            OSDisk = new VirtualMachineOSDisk(DiskCreateOptionType.FromImage)
                            {
                                OSType = SupportedOperatingSystemType.Linux,
                                Caching = CachingType.ReadWrite,
                                ManagedDisk = new VirtualMachineManagedDisk()
                                {
                                    StorageAccountType = StorageAccountType.StandardLrs
                                }
                            },
                            ImageReference = new ImageReference()
                            {
                                Publisher = "Canonical",
                                Offer = "UbuntuServer",
                                Sku = "16.04-LTS",
                                Version = "latest",
                            },
                        },
                        Zones =
                    {
                        "1"
                    },
                        BootDiagnostics = new BootDiagnostics()
                        {
                            StorageUri = new Uri($"http://{storageAccountCreatable.Data.Name}.blob.core.windows.net")
                        }
                    };
                    var creatableVirtualMachine = virtualMachineCollection.CreateOrUpdate(WaitUntil.Completed, "vm-"+i, linuxVmdata).Value;
                    creatableVirtualMachines.Add(creatableVirtualMachine);
                }

                var startTime = DateTimeOffset.Now.UtcDateTime;
                Utilities.Log("Creating the virtual machines");

                Utilities.Log("Created virtual machines");

                var virtualMachines = virtualMachineCollection.GetAll();

                foreach (var virtualMachine in virtualMachines)
                {
                    Utilities.Log(virtualMachine.Id);
                }

                var endTime = DateTimeOffset.Now.UtcDateTime;

                Utilities.Log($"Created VM: took {(endTime - startTime).TotalSeconds} seconds");
            }
            finally
            {
                Utilities.Log($"Deleting resource group : {rgName}");
                resourceGroup.Delete(WaitUntil.Completed);
                Utilities.Log($"Deleted resource group : {rgName}");
            }
        }

        public static void Main(string[] args)
        {
            try
            {
                //=============================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                // Print selected subscription
                Utilities.Log("Selected subscription: " + client.GetSubscriptions().Id);

                RunSample(client);
            }
            catch (Exception ex)
            {
                Utilities.Log(ex);
            }
        }
    }
}