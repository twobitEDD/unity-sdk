using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Numerics;
using UnityEngine;
using Newtonsoft.Json;
using TokenERC721Contract = Thirdweb.Contracts.TokenERC721.ContractDefinition;
using DropERC721Contract = Thirdweb.Contracts.DropERC721.ContractDefinition;
using ERC721AQueryable = Thirdweb.Contracts.ERC721AQueryableUpgradeable.ContractDefinition;
using SignatureDropContract = Thirdweb.Contracts.SignatureDrop.ContractDefinition;
using System.Linq;

namespace Thirdweb
{
    /// <summary>
    /// Interact with any ERC721 compatible contract.
    /// </summary>
    public class ERC721 : Routable
    {
        /// <summary>
        /// Handle signature minting functionality
        /// </summary>
        public ERC721Signature signature;

        /// <summary>
        /// Query claim conditions
        /// </summary>
        public ERC721ClaimConditions claimConditions;

        private readonly string contractAddress;

        /// <summary>
        /// Interact with any ERC721 compatible contract.
        /// </summary>
        public ERC721(string parentRoute, string contractAddress)
            : base(Routable.append(parentRoute, "erc721"))
        {
            this.contractAddress = contractAddress;
            this.signature = new ERC721Signature(baseRoute, contractAddress);
            this.claimConditions = new ERC721ClaimConditions(baseRoute, contractAddress);
        }

        // READ FUNCTIONS

        /// <summary>
        /// Get a NFT in this contract by its ID
        /// </summary>
        public async Task<NFT> Get(string tokenId)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<NFT>(getRoute("get"), Utils.ToJsonStringArray(tokenId));
            }
            else
            {
                var tokenURI = await TransactionManager.ThirdwebRead<TokenERC721Contract.TokenURIFunction, TokenERC721Contract.TokenURIOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.TokenURIFunction() { TokenId = BigInteger.Parse(tokenId) }
                );

                tokenURI.ReturnValue1 = tokenURI.ReturnValue1.Contains("0x{id}") ? tokenURI.ReturnValue1.Replace("0x{id}", tokenId) : tokenURI.ReturnValue1;

                var nft = new NFT
                {
                    owner = await OwnerOf(tokenId),
                    type = "ERC721",
                    supply = await TotalCount(),
                    quantityOwned = 1,
                    metadata = await ThirdwebManager.Instance.SDK.storage.DownloadText<NFTMetadata>(tokenURI.ReturnValue1)
                };
                nft.metadata.image = nft.metadata.image.ReplaceIPFS();
                nft.metadata.id = tokenId;
                nft.metadata.uri = tokenURI.ReturnValue1.ReplaceIPFS();
                return nft;
            }
        }

        /// <summary>
        /// Get a all NFTs in this contract
        /// </summary>
        public async Task<List<NFT>> GetAll(QueryAllParams queryParams = null)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<List<NFT>>(getRoute("getAll"), Utils.ToJsonStringArray(queryParams));
            }
            else
            {
                int totalCount = await TotalCount();
                int start = queryParams?.start ?? 0;
                int count = queryParams?.count + 1 ?? totalCount;
                int end = Math.Min(start + count, totalCount);
                List<NFT> allNfts = new();
                try
                {
                    var uriFunctions = Enumerable.Range(start, end - start).Select(i => new TokenERC721Contract.TokenURIFunction() { TokenId = new BigInteger(i) }).ToArray();
                    var uriResults = await TransactionManager.ThirdwebMulticallRead<TokenERC721Contract.TokenURIFunction, TokenERC721Contract.TokenURIOutputDTO>(contractAddress, uriFunctions);
                    var metadataFetchTasks = new List<Task<NFTMetadata>>();
                    for (int i = 0; i < uriResults.Length; i++)
                    {
                        var tokenUri = uriResults[i].ReturnValue1.Replace("0x{id}", uriFunctions[i].TokenId.ToString()).ReplaceIPFS();
                        metadataFetchTasks.Add(ThirdwebManager.Instance.SDK.storage.DownloadText<NFTMetadata>(tokenUri));
                    }
                    var metadataResults = await Task.WhenAll(metadataFetchTasks);
                    allNfts = new List<NFT>();
                    for (int i = 0; i < metadataResults.Length; i++)
                    {
                        var tokenId = uriFunctions[i].TokenId.ToString();
                        var metadata = metadataResults[i];
                        metadata.image = metadata.image.ReplaceIPFS();
                        metadata.id = tokenId;
                        metadata.uri = uriResults[i].ReturnValue1.ReplaceIPFS();
                        var nft = new NFT
                        {
                            owner = await OwnerOf(tokenId),
                            type = "ERC721",
                            supply = 1,
                            quantityOwned = 1,
                            metadata = metadata
                        };

                        allNfts.Add(nft);
                    }
                }
                catch
                {
                    ThirdwebDebug.LogWarning("Unable to fetch using Multicall3, likely not deployed on this chain, falling back to single queries.");
                    for (int i = start; i <= end; i++)
                        allNfts.Add(await Get(i.ToString()));
                }
                return allNfts;
            }
        }

        /// <summary>
        /// Get a all NFTs owned by the connected wallet
        /// </summary>
        /// <param name="address">Optional wallet address to query NFTs of</param>
        public async Task<List<NFT>> GetOwned(string address = null)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<List<NFT>>(getRoute("getOwned"), Utils.ToJsonStringArray(address));
            }
            else
            {
                string owner = address ?? await ThirdwebManager.Instance.SDK.wallet.GetAddress();
                var balanceOfOwner = int.Parse(await BalanceOf(owner));
                var ownedNfts = new List<NFT>();

                try
                {
                    // ERC721Enumerable
                    for (int i = 0; i < balanceOfOwner; i++)
                    {
                        var tokenId = await TransactionManager.ThirdwebRead<TokenERC721Contract.TokenOfOwnerByIndexFunction, TokenERC721Contract.TokenOfOwnerByIndexOutputDTO>(
                            contractAddress,
                            new TokenERC721Contract.TokenOfOwnerByIndexFunction() { Owner = owner, Index = (BigInteger)i }
                        );
                        ownedNfts.Add(await Get(tokenId.ReturnValue1.ToString()));
                    }
                    return ownedNfts;
                }
                catch
                {
                    // ERC721AQueryable
                    try
                    {
                        var tokensOwned = await TransactionManager.ThirdwebRead<ERC721AQueryable.TokensOfOwnerFunction, ERC721AQueryable.TokensOfOwnerOutputDTO>(
                            contractAddress,
                            new ERC721AQueryable.TokensOfOwnerFunction() { Owner = owner }
                        );
                        for (int i = 0; i < tokensOwned.ReturnValue1.Count; i++)
                        {
                            ownedNfts.Add(await Get(tokensOwned.ReturnValue1[i].ToString()));
                        }
                        return ownedNfts;
                    }
                    catch
                    {
                        // ERC721Supply
                        var count = await TotalCount();
                        for (int i = 0; i < count; i++)
                        {
                            if (await OwnerOf(i.ToString()) == owner)
                            {
                                ownedNfts.Add(await Get(i.ToString()));
                                if (ownedNfts.Count == balanceOfOwner)
                                    break;
                            }
                        }
                        return ownedNfts;
                    }
                }
            }
        }

        /// <summary>
        /// Get the owner of a NFT in this contract
        /// </summary>
        public async Task<string> OwnerOf(string tokenId)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<string>(getRoute("ownerOf"), Utils.ToJsonStringArray(tokenId));
            }
            else
            {
                try
                {
                    var tokenURI = await TransactionManager.ThirdwebRead<TokenERC721Contract.OwnerOfFunction, TokenERC721Contract.OwnerOfOutputDTO>(
                        contractAddress,
                        new TokenERC721Contract.OwnerOfFunction() { TokenId = BigInteger.Parse(tokenId) }
                    );
                    return tokenURI.ReturnValue1;
                }
                catch (System.Exception)
                {
                    ThirdwebDebug.LogWarning($"Unable to find owner of {tokenId}, return address(0)");
                    return "0x0000000000000000000000000000000000000000";
                }
            }
        }

        /// <summary>
        /// Get the balance of NFTs in this contract for the connected wallet
        /// </summary>
        public async Task<string> Balance()
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<string>(getRoute("balance"), new string[] { });
            }
            else
            {
                return await BalanceOf(await ThirdwebManager.Instance.SDK.wallet.GetAddress());
            }
        }

        /// <summary>
        /// Get the balance of NFTs in this contract for the given wallet address
        /// </summary>
        public async Task<string> BalanceOf(string address)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<string>(getRoute("balanceOf"), Utils.ToJsonStringArray(address));
            }
            else
            {
                var balance = await TransactionManager.ThirdwebRead<TokenERC721Contract.BalanceOfFunction, TokenERC721Contract.BalanceOfOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.BalanceOfFunction() { Owner = address }
                );
                return balance.ReturnValue1.ToString();
            }
        }

        /// <summary>
        /// Check whether the given contract address has been approved to transfer NFTs on behalf of the given wallet address
        /// </summary>
        /// <param name="address">The wallet address</param>
        /// <param name="contractAddress">The contract address to check approval for</param>
        public async Task<bool> IsApprovedForAll(string address, string approvedContract)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<bool>(getRoute("isApproved"), Utils.ToJsonStringArray(address, approvedContract));
            }
            else
            {
                var isApprovedForAll = await TransactionManager.ThirdwebRead<TokenERC721Contract.IsApprovedForAllFunction, TokenERC721Contract.IsApprovedForAllOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.IsApprovedForAllFunction() { Owner = address, Operator = approvedContract }
                );
                return isApprovedForAll.ReturnValue1;
            }
        }

        /// <summary>
        /// Get the total suppply in circulation
        /// </summary>
        public async Task<int> TotalCount()
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<int>(getRoute("totalCount"), new string[] { });
            }
            else
            {
                var totalCount = await TransactionManager.ThirdwebRead<TokenERC721Contract.TotalSupplyFunction, TokenERC721Contract.TotalSupplyOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.TotalSupplyFunction() { }
                );
                return (int)totalCount.ReturnValue1;
            }
        }

        /// <summary>
        /// Get the total claimed suppply for Drop contracts
        /// </summary>
        public async Task<int> TotalClaimedSupply()
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<int>(getRoute("totalClaimedSupply"), new string[] { });
            }
            else
            {
                throw new UnityException("This functionality is not yet available on your current platform.");
            }
        }

        /// <summary>
        /// Get the total unclaimed suppply for Drop contracts
        /// </summary>
        public async Task<int> TotalUnclaimedSupply()
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<int>(getRoute("totalUnclaimedSupply"), new string[] { });
            }
            else
            {
                throw new UnityException("This functionality is not yet available on your current platform.");
            }
        }

        // WRITE FUNCTIONS

        /// <summary>
        /// Set approval to the given contract to transfer NFTs on behalf of the connected wallet
        /// </summary>
        public async Task<TransactionResult> SetApprovalForAll(string contractToApprove, bool approved)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("setApprovalForAll"), Utils.ToJsonStringArray(contractToApprove, approved));
            }
            else
            {
                return await TransactionManager.ThirdwebWrite(contractAddress, new TokenERC721Contract.SetApprovalForAllFunction() { Operator = contractToApprove, Approved = approved });
            }
        }

        /// <summary>
        /// Transfer a given NFT to the given address
        /// </summary>
        public async Task<TransactionResult> Transfer(string to, string tokenId)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("transfer"), Utils.ToJsonStringArray(to, tokenId));
            }
            else
            {
                return await TransactionManager.ThirdwebWrite(
                    contractAddress,
                    new TokenERC721Contract.TransferFromFunction()
                    {
                        From = await ThirdwebManager.Instance.SDK.wallet.GetAddress(),
                        To = to,
                        TokenId = BigInteger.Parse(tokenId)
                    }
                );
            }
        }

        /// <summary>
        /// Burn a given NFT
        /// </summary>
        public async Task<TransactionResult> Burn(string tokenId)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("burn"), Utils.ToJsonStringArray(tokenId));
            }
            else
            {
                return await TransactionManager.ThirdwebWrite(contractAddress, new TokenERC721Contract.BurnFunction() { TokenId = BigInteger.Parse(tokenId) });
            }
        }

        /// <summary>
        /// Claim NFTs from a Drop contract
        /// </summary>
        public async Task<TransactionResult[]> Claim(int quantity)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult[]>(getRoute("claim"), Utils.ToJsonStringArray(quantity));
            }
            else
            {
                return await ClaimTo(await ThirdwebManager.Instance.SDK.wallet.GetAddress(), quantity);
            }
        }

        /// <summary>
        /// Claim NFTs from a Drop contract and send them to the given address
        /// </summary>
        public async Task<TransactionResult[]> ClaimTo(string address, int quantity)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult[]>(getRoute("claimTo"), Utils.ToJsonStringArray(address, quantity));
            }
            else
            {
                var claimCondition = await claimConditions.GetActive();
                BigInteger rawPrice = BigInteger.Parse(claimCondition.currencyMetadata.value);
                return new TransactionResult[]
                {
                    await TransactionManager.ThirdwebWrite(
                        contractAddress,
                        new DropERC721Contract.ClaimFunction()
                        {
                            Receiver = address,
                            Quantity = quantity,
                            Currency = claimCondition.currencyAddress,
                            PricePerToken = rawPrice,
                            AllowlistProof = new DropERC721Contract.AllowlistProof
                            {
                                Proof = new List<byte[]>(),
                                Currency = claimCondition.currencyAddress,
                                PricePerToken = rawPrice,
                                QuantityLimitPerWallet = BigInteger.Parse(claimCondition.maxClaimablePerWallet),
                            }, // TODO add support for allowlists
                            Data = new byte[] { }
                        },
                        claimCondition.currencyAddress == Utils.NativeTokenAddress ? quantity * rawPrice : 0
                    )
                };
            }
        }

        /// <summary>
        /// Mint an NFT (requires minting permission)
        /// </summary>
        public async Task<TransactionResult> Mint(NFTMetadata nft)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("mint"), Utils.ToJsonStringArray(nft));
            }
            else
            {
                return await MintTo(await ThirdwebManager.Instance.SDK.wallet.GetAddress(), nft);
            }
        }

        /// <summary>
        /// Mint an NFT and send it to the given wallet (requires minting permission)
        /// </summary>
        public async Task<TransactionResult> MintTo(string address, NFTMetadata nft)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("mintTo"), Utils.ToJsonStringArray(address, nft));
            }
            else
            {
                var uri = await ThirdwebManager.Instance.SDK.storage.UploadText(JsonConvert.SerializeObject(nft));
                return await TransactionManager.ThirdwebWrite(contractAddress, new TokenERC721Contract.MintToFunction() { To = address, Uri = uri.IpfsHash.CidToIpfsUrl() });
            }
        }
    }

    /// <summary>
    /// Fetch claim conditions for a given ERC721 drop contract
    /// </summary>
    public class ERC721ClaimConditions : Routable
    {
        private readonly string contractAddress;

        public ERC721ClaimConditions(string parentRoute, string contractAddress)
            : base(Routable.append(parentRoute, "claimConditions"))
        {
            this.contractAddress = contractAddress;
        }

        /// <summary>
        /// Get the active claim condition
        /// </summary>
        public async Task<ClaimConditions> GetActive()
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<ClaimConditions>(getRoute("getActive"), new string[] { });
            }
            else
            {
                DropERC721Contract.ClaimCondition data;
                try
                {
                    var id = await TransactionManager.ThirdwebRead<DropERC721Contract.GetActiveClaimConditionIdFunction, DropERC721Contract.GetActiveClaimConditionIdOutputDTO>(
                        contractAddress,
                        new DropERC721Contract.GetActiveClaimConditionIdFunction() { }
                    );

                    data = (
                        await TransactionManager.ThirdwebRead<DropERC721Contract.GetClaimConditionByIdFunction, DropERC721Contract.GetClaimConditionByIdOutputDTO>(
                            contractAddress,
                            new DropERC721Contract.GetClaimConditionByIdFunction() { ConditionId = id.ReturnValue1 }
                        )
                    ).Condition;
                }
                catch
                {
                    var sigDropCondition = await TransactionManager.ThirdwebRead<SignatureDropContract.ClaimConditionFunction, SignatureDropContract.ClaimConditionOutputDTO>(
                        contractAddress,
                        new SignatureDropContract.ClaimConditionFunction() { }
                    );

                    data = new DropERC721Contract.ClaimCondition()
                    {
                        StartTimestamp = sigDropCondition.StartTimestamp,
                        MaxClaimableSupply = sigDropCondition.MaxClaimableSupply,
                        SupplyClaimed = sigDropCondition.SupplyClaimed,
                        QuantityLimitPerWallet = sigDropCondition.QuantityLimitPerWallet,
                        MerkleRoot = sigDropCondition.MerkleRoot,
                        PricePerToken = sigDropCondition.PricePerToken,
                        Currency = sigDropCondition.Currency,
                        Metadata = sigDropCondition.Metadata,
                    };
                }

                var currency = new Currency();
                try
                {
                    currency = await ThirdwebManager.Instance.SDK.GetContract(data.Currency).ERC20.Get();
                }
                catch
                {
                    ThirdwebDebug.Log("Could not fetch currency metadata, proceeding without it.");
                }

                return new ClaimConditions()
                {
                    availableSupply = (data.MaxClaimableSupply - data.SupplyClaimed).ToString(),
                    currencyAddress = data.Currency,
                    currencyMetadata = new CurrencyValue(
                        currency.name,
                        currency.symbol,
                        currency.decimals,
                        data.PricePerToken.ToString(),
                        data.PricePerToken.ToString().FormatERC20(4, int.Parse(currency.decimals), true)
                    ),
                    currentMintSupply = data.SupplyClaimed.ToString(),
                    maxClaimablePerWallet = data.QuantityLimitPerWallet.ToString(),
                    maxClaimableSupply = data.MaxClaimableSupply.ToString(),
                };
            }
        }

        /// <summary>
        /// Check whether the connected wallet is eligible to claim
        /// </summary>
        public async Task<bool> CanClaim(int quantity, string addressToCheck = null)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<bool>(getRoute("canClaim"), Utils.ToJsonStringArray(quantity, addressToCheck));
            }
            else
            {
                throw new UnityException("This functionality is not yet available on your current platform.");
            }
        }

        /// <summary>
        /// Get the reasons why the connected wallet is not eligible to claim
        /// </summary>
        public async Task<string[]> GetIneligibilityReasons(int quantity, string addressToCheck = null)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<string[]>(getRoute("getClaimIneligibilityReasons"), Utils.ToJsonStringArray(quantity, addressToCheck));
            }
            else
            {
                throw new UnityException("This functionality is not yet available on your current platform.");
            }
        }

        /// <summary>
        /// Get the special values set in the allowlist for the given wallet
        /// </summary>
        public async Task<bool> GetClaimerProofs(string claimerAddress)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<bool>(getRoute("getClaimerProofs"), Utils.ToJsonStringArray(claimerAddress));
            }
            else
            {
                throw new UnityException("This functionality is not yet available on your current platform.");
            }
        }
    }

    [System.Serializable]
#nullable enable
    public class ERC721MintPayload
    {
        public string to;
        public string price;
        public string currencyAddress;
        public string primarySaleRecipient;
        public string royaltyRecipient;
        public int royaltyBps;
        public int quantity;
        public NFTMetadata metadata;
        public string uid;
        public long mintStartTime;
        public long mintEndTime;

        public ERC721MintPayload(string receiverAddress, NFTMetadata metadata)
        {
            this.metadata = metadata;
            this.to = receiverAddress;
            this.price = "0";
            this.currencyAddress = Utils.AddressZero;
            this.primarySaleRecipient = Utils.AddressZero;
            this.royaltyRecipient = Utils.AddressZero;
            this.royaltyBps = 0;
            this.quantity = 1;
            this.uid = Utils.ToBytes32HexString(Guid.NewGuid().ToByteArray());
            this.mintStartTime = Utils.GetUnixTimeStampNow() - 60;
            this.mintEndTime = Utils.GetUnixTimeStampIn10Years();
        }
    }

    [System.Serializable]
    public struct ERC721SignedPayloadOutput
    {
        public string to;
        public string price;
        public string currencyAddress;
        public string primarySaleRecipient;
        public string royaltyRecipient;
        public int royaltyBps;
        public int quantity;
        public string uri;
        public string uid;
        public long mintStartTime;
        public long mintEndTime;
    }

    [System.Serializable]
    public struct ERC721SignedPayload
    {
        public string signature;
        public ERC721SignedPayloadOutput payload;
    }

    /// <summary>
    /// Generate, verify and mint signed mintable payloads
    /// </summary>
    public class ERC721Signature : Routable
    {
        private readonly string contractAddress;

        /// <summary>
        /// Generate, verify and mint signed mintable payloads
        /// </summary>
        public ERC721Signature(string parentRoute, string contractAddress)
            : base(Routable.append(parentRoute, "signature"))
        {
            this.contractAddress = contractAddress;
        }

        /// <summary>
        /// Generate a signed mintable payload. Requires minting permission.
        /// </summary>
        public async Task<ERC721SignedPayload> Generate(ERC721MintPayload payloadToSign, string privateKeyOverride = "")
        {
            if (Utils.IsWebGLBuild())
            {
                var signedPayload = await Bridge.InvokeRoute<ERC721SignedPayload>(getRoute("generate"), Utils.ToJsonStringArray(payloadToSign));

                if (privateKeyOverride == "")
                    return signedPayload;

                var req = new TokenERC721Contract.MintRequest()
                {
                    To = payloadToSign.to,
                    RoyaltyRecipient = signedPayload.payload.royaltyRecipient,
                    RoyaltyBps = signedPayload.payload.royaltyBps,
                    PrimarySaleRecipient = signedPayload.payload.primarySaleRecipient,
                    Uri = signedPayload.payload.uri,
                    Price = BigInteger.Parse(payloadToSign.price.ToWei()),
                    Currency = payloadToSign.currencyAddress,
                    ValidityStartTimestamp = payloadToSign.mintStartTime,
                    ValidityEndTimestamp = payloadToSign.mintEndTime,
                    Uid = payloadToSign.uid.HexStringToByteArray()
                };

                string signature = await Thirdweb.EIP712.GenerateSignature_TokenERC721(
                    "TokenERC721",
                    "1",
                    await ThirdwebManager.Instance.SDK.wallet.GetChainId(),
                    contractAddress,
                    req,
                    string.IsNullOrEmpty(privateKeyOverride) ? null : privateKeyOverride
                );

                signedPayload = new ERC721SignedPayload()
                {
                    signature = signature,
                    payload = new ERC721SignedPayloadOutput()
                    {
                        to = req.To,
                        price = req.Price.ToString(),
                        currencyAddress = req.Currency,
                        primarySaleRecipient = req.PrimarySaleRecipient,
                        royaltyRecipient = req.RoyaltyRecipient,
                        royaltyBps = (int)req.RoyaltyBps,
                        quantity = 1,
                        uri = req.Uri,
                        uid = req.Uid.ByteArrayToHexString(),
                        mintStartTime = (long)req.ValidityStartTimestamp,
                        mintEndTime = (long)req.ValidityEndTimestamp
                    }
                };

                return signedPayload;
            }
            else
            {
                var uri = await ThirdwebManager.Instance.SDK.storage.UploadText(JsonConvert.SerializeObject(payloadToSign.metadata));
                var royaltyInfo = await TransactionManager.ThirdwebRead<TokenERC721Contract.GetDefaultRoyaltyInfoFunction, TokenERC721Contract.GetDefaultRoyaltyInfoOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.GetDefaultRoyaltyInfoFunction() { }
                );
                var primarySaleRecipient = await TransactionManager.ThirdwebRead<TokenERC721Contract.PrimarySaleRecipientFunction, TokenERC721Contract.PrimarySaleRecipientOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.PrimarySaleRecipientFunction() { }
                );

                var req = new TokenERC721Contract.MintRequest()
                {
                    To = payloadToSign.to,
                    RoyaltyRecipient = royaltyInfo.ReturnValue1,
                    RoyaltyBps = royaltyInfo.ReturnValue2,
                    PrimarySaleRecipient = primarySaleRecipient.ReturnValue1,
                    Uri = uri.IpfsHash.CidToIpfsUrl(),
                    Price = BigInteger.Parse(payloadToSign.price.ToWei()),
                    Currency = payloadToSign.currencyAddress,
                    ValidityStartTimestamp = payloadToSign.mintStartTime,
                    ValidityEndTimestamp = payloadToSign.mintEndTime,
                    Uid = payloadToSign.uid.HexStringToByteArray()
                };

                string signature = await Thirdweb.EIP712.GenerateSignature_TokenERC721(
                    "TokenERC721",
                    "1",
                    await ThirdwebManager.Instance.SDK.wallet.GetChainId(),
                    contractAddress,
                    req,
                    string.IsNullOrEmpty(privateKeyOverride) ? null : privateKeyOverride
                );

                var signedPayload = new ERC721SignedPayload()
                {
                    signature = signature,
                    payload = new ERC721SignedPayloadOutput()
                    {
                        to = req.To,
                        price = req.Price.ToString(),
                        currencyAddress = req.Currency,
                        primarySaleRecipient = req.PrimarySaleRecipient,
                        royaltyRecipient = req.RoyaltyRecipient,
                        royaltyBps = (int)req.RoyaltyBps,
                        quantity = 1,
                        uri = req.Uri,
                        uid = req.Uid.ByteArrayToHexString(),
                        mintStartTime = (long)req.ValidityStartTimestamp,
                        mintEndTime = (long)req.ValidityEndTimestamp
                    }
                };
                return signedPayload;
            }
        }

        /// <summary>
        /// Verify that a signed mintable payload is valid
        /// </summary>
        public async Task<bool> Verify(ERC721SignedPayload signedPayload)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<bool>(getRoute("verify"), Utils.ToJsonStringArray(signedPayload));
            }
            else
            {
                var verify = await TransactionManager.ThirdwebRead<TokenERC721Contract.VerifyFunction, TokenERC721Contract.VerifyOutputDTO>(
                    contractAddress,
                    new TokenERC721Contract.VerifyFunction()
                    {
                        Req = new TokenERC721Contract.MintRequest()
                        {
                            To = signedPayload.payload.to,
                            RoyaltyRecipient = signedPayload.payload.royaltyRecipient,
                            RoyaltyBps = (BigInteger)signedPayload.payload.royaltyBps,
                            PrimarySaleRecipient = signedPayload.payload.primarySaleRecipient,
                            Uri = signedPayload.payload.uri,
                            Price = BigInteger.Parse(signedPayload.payload.price),
                            Currency = signedPayload.payload.currencyAddress,
                            ValidityStartTimestamp = signedPayload.payload.mintStartTime,
                            ValidityEndTimestamp = signedPayload.payload.mintEndTime,
                            Uid = signedPayload.payload.uid.HexStringToByteArray()
                        },
                        Signature = signedPayload.signature.HexStringToByteArray()
                    }
                );
                return verify.ReturnValue1;
            }
        }

        /// <summary>
        /// Mint a signed mintable payload
        /// </summary>
        public async Task<TransactionResult> Mint(ERC721SignedPayload signedPayload)
        {
            if (Utils.IsWebGLBuild())
            {
                return await Bridge.InvokeRoute<TransactionResult>(getRoute("mint"), Utils.ToJsonStringArray(signedPayload));
            }
            else
            {
                return await TransactionManager.ThirdwebWrite(
                    contractAddress,
                    new TokenERC721Contract.MintWithSignatureFunction()
                    {
                        Req = new TokenERC721Contract.MintRequest()
                        {
                            To = signedPayload.payload.to,
                            RoyaltyRecipient = signedPayload.payload.royaltyRecipient,
                            RoyaltyBps = (BigInteger)signedPayload.payload.royaltyBps,
                            PrimarySaleRecipient = signedPayload.payload.primarySaleRecipient,
                            Uri = signedPayload.payload.uri,
                            Price = BigInteger.Parse(signedPayload.payload.price),
                            Currency = signedPayload.payload.currencyAddress,
                            ValidityStartTimestamp = signedPayload.payload.mintStartTime,
                            ValidityEndTimestamp = signedPayload.payload.mintEndTime,
                            Uid = signedPayload.payload.uid.HexStringToByteArray()
                        },
                        Signature = signedPayload.signature.HexStringToByteArray()
                    },
                    signedPayload.payload.quantity * BigInteger.Parse(signedPayload.payload.price)
                );
            }
        }
    }
}
