﻿using NBitcoin;
using Newtonsoft.Json.Linq;
using NBitcoin.RPC;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;
using System.Threading;
using Microsoft.Extensions.Logging;
using NBXplorer.Backend;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using NBitcoin.Crypto;
using NBXplorer.Models;

namespace NBXplorer
{
	public class GetBlockchainInfoResponse
	{
		[JsonProperty("headers")]
		public int Headers
		{
			get; set;
		}
		[JsonProperty("blocks")]
		public int Blocks
		{
			get; set;
		}
		[JsonProperty("verificationprogress")]
		public double VerificationProgress
		{
			get; set;
		}

		[JsonProperty("mediantime")]
		public long? MedianTime
		{
			get; set;
		}

		[JsonProperty("initialblockdownload")]
		public bool? InitialBlockDownload
		{
			get; set;
		}
		[JsonProperty("bestblockhash")]
		[JsonConverter(typeof(NBitcoin.JsonConverters.UInt256JsonConverter))]
		public uint256 BestBlockHash { get; set; }
	}

	public class GetNetworkInfoResponse
	{
		public class LocalAddress
		{
			public string address { get; set; }
			public int port { get; set; }
		}
		public double? relayfee
		{
			get; set;
		}
		public FeeRate GetRelayFee()
		{
			return relayfee == null ? null : new FeeRate(Money.Coins((decimal)relayfee), 1000);
		}
		public double? incrementalfee
		{
			get; set;
		}
		public FeeRate GetIncrementalFee()
		{
			return incrementalfee == null ? null : new FeeRate(Money.Coins((decimal)incrementalfee), 1000);
		}
		public LocalAddress[] localaddresses
		{
			get; set;
		}
	}

	public static class RPCClientExtensions
	{
		public static async Task<ScanTxoutSetResponse> StartScanTxoutSetExAsync(this RPCClient rpc, ScanTxoutSetParameters parameters, CancellationToken cancellationToken)
		{
			int delay = 100;
			retry:
			try
			{
				return await rpc.StartScanTxoutSetAsync(parameters, cancellationToken);
			}
			catch (RPCException ex) when (!cancellationToken.IsCancellationRequested && ex.Message.StartsWith("Scan already in progress", StringComparison.OrdinalIgnoreCase))
			{
				await Task.Delay(delay, cancellationToken);
				delay = Math.Max(delay * 2, 10_000);
				goto retry;
			}
		}
		public static async Task<bool?> SupportTxIndex(this RPCClient rpc)
		{
			try
			{
				var result = await rpc.SendCommandAsync(new RPCRequest("getindexinfo", new[] { "txindex" }) { ThrowIfRPCError = false });
				if (result.Error != null)
					return null;
				return result.Result["txindex"] is not null;
			}
			catch
			{
				return null;
			}
		}
		public static async Task<bool> WarmupBlockchain(this RPCClient rpc, ILogger logger)
		{
			if (await rpc.GetBlockCountAsync() < rpc.Network.Consensus.CoinbaseMaturity)
			{
				logger.LogInformation($"Less than {rpc.Network.Consensus.CoinbaseMaturity} blocks, mining some block for regtest (you can disable with NBXPLORER_NOWARMUP=1)");
				await rpc.EnsureGenerateAsync(rpc.Network.Consensus.CoinbaseMaturity + 1);
				return true;
			}
			else
			{
				var hash = await rpc.GetBestBlockHashAsync();

				BlockHeader header = null;
				try
				{
					header = await rpc.GetBlockHeaderAsync(hash);
				}
				catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND)
				{
					header = (await rpc.GetBlockAsync(hash)).Header;
				}
				if ((DateTimeOffset.UtcNow - header.BlockTime) > TimeSpan.FromSeconds(24 * 60 * 60))
				{
					logger.LogInformation($"It has been a while nothing got mined on regtest... mining 10 blocks");
					await rpc.GenerateAsync(10);
					return true;
				}
				return false;
			}
		}

		static FeeRate GetFeeRate(Money feePaid, int size) =>
							(feePaid, size) is (Money f, not 0) ? new FeeRate(f, size) : null;
		public static TransactionMetadata ToTransactionMetadata(this MempoolEntry entry)
		=> new()
		{
			Fees = entry.BaseFee,
			VirtualSize = entry.VirtualSizeBytes,
			FeeRate = GetFeeRate(entry.BaseFee, entry.VirtualSizeBytes)
		};

		// This method fetch some information from getmempoolentry which may be useful for analysis, it's not critical to have it, so we don't want to fail the whole thing if it fails.
		public static async Task<Dictionary<uint256, MempoolEntry>> FetchMempoolInfo(this RPCClient rpc,  IEnumerable<uint256> txHashes, CancellationToken cancellationToken)
		{
			var batch = rpc.PrepareBatch();
			var tasks = new List<(uint256 Id, Task<MempoolEntry> MempoolEntry)>();
			var metadatas = new Dictionary<uint256, MempoolEntry>();
			foreach (var id in txHashes)
			{
				tasks.Add((id, batch.GetMempoolEntryAsync(id, false, cancellationToken)));
			}
			if (tasks.Count == 0)
				return metadatas;
			try
			{
				await batch.SendBatchAsync(cancellationToken);
				foreach (var t in tasks)
				{
					try
					{
						var entry = await t.MempoolEntry;
						if (entry is null) continue;
						metadatas.TryAdd(t.Id, entry);
					}
					catch
					{
					}
				}
			}
			// If it fails, that's OK, we don't care about the mempool entry information that much
			catch
			{
			}
			return metadatas;
		}
		public static bool IsWhitelisted(this PeerInfo peer)
		{
			if (peer is null)
				return false;
			if (peer.IsWhiteListed)
				return true;
			if (peer.Permissions.Contains("noban", StringComparer.OrdinalIgnoreCase))
				return true;
			return false;
		}
		public static bool IsSynching(this GetBlockchainInfoResponse blockchainInfo, NBXplorerNetwork network)
		{
			// When no block has been mined, core think it is synching, but it's just there is no block
			if (blockchainInfo.Headers == 0 && network.NBitcoinNetwork.ChainName == ChainName.Regtest)
				return false;
			if (blockchainInfo.InitialBlockDownload == true)
				return true;
			if (blockchainInfo.MedianTime.HasValue && network.NBitcoinNetwork.ChainName != ChainName.Regtest)
			{
				var time = NBitcoin.Utils.UnixTimeToDateTime(blockchainInfo.MedianTime.Value);
				// 5 month diff? probably synching...
				if (DateTimeOffset.UtcNow - time > TimeSpan.FromDays(30 * 5))
				{
					return true;
				}
			}

			return blockchainInfo.Headers - blockchainInfo.Blocks > 6;
		}
		public static async Task<GetBlockchainInfoResponse> GetBlockchainInfoAsyncEx(this RPCClient client, CancellationToken cancellationToken = default)
		{
			var result = await client.SendCommandAsync("getblockchaininfo", cancellationToken).ConfigureAwait(false);
			return JsonConvert.DeserializeObject<GetBlockchainInfoResponse>(result.ResultString);
		}

		public static async Task EnsureWalletCreated(this RPCClient client, ILogger logger)
		{
			var network = client.Network.NetworkSet;
			var walletName = client.CredentialString.WalletName ?? "";
			bool created = false;
			try
			{
				await client.CreateWalletAsync(walletName, new CreateWalletOptions()
				{
					LoadOnStartup = true,
					Blank = client.Network.ChainName != ChainName.Regtest
				});
				logger.LogInformation($"{network.CryptoCode}: Created RPC wallet \"{walletName}\"");
				created = true;
			}
			catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND)
			{
				// Not supported
				return;
			}
			catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_WALLET_ERROR || ex.RPCCode == RPCErrorCode.RPC_WALLET_ALREADY_EXISTS)
			{
				// Already exists, let's load it
			}
			catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized || ex.StatusCode is HttpStatusCode.Forbidden)
			{
				// Not allowed, which is fine
				return;
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, $"{network.CryptoCode}: Failed to create a RPC wallet with unknown error, skipping...");
			}
			try
			{
				await client.LoadWalletAsync(walletName, true);
				logger.LogInformation($"{network.CryptoCode}: RPC Wallet loaded");
			}
			catch (RPCException ex) when (ex.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND || ex.RPCCode == RPCErrorCode.RPC_WALLET_ERROR || ex.RPCCode == RPCErrorCode.RPC_WALLET_ALREADY_LOADED)
			{
				// Not supported, or already loaded? Just ignore.
			}
			catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized || ex.StatusCode is HttpStatusCode.Forbidden)
			{
				// Not allowed, which is fine
			}
			catch when (!created)
			{
				// Let's skip, a rpc wallet isn't essential
			}
		}

		public static async Task<GetNetworkInfoResponse> GetNetworkInfoAsync(this RPCClient client)
		{
			var result = await client.SendCommandAsync("getnetworkinfo").ConfigureAwait(false);
			return JsonConvert.DeserializeObject<GetNetworkInfoResponse>(result.ResultString);
		}

		public static async Task<PSBT> UTXOUpdatePSBT(this RPCClient rpcClient, PSBT psbt)
		{
			if (psbt == null) throw new ArgumentNullException(nameof(psbt));
			var response = await rpcClient.SendCommandAsync("utxoupdatepsbt", new object[] { psbt.ToBase64() });
			response.ThrowIfError();
			if (response.Error == null && response.Result is JValue rpcResult && rpcResult.Value is string psbtStr)
			{
				return PSBT.Parse(psbtStr, psbt.Network);
			}

			throw new Exception("This should never happen");
		}
		public static async Task<BlockHeaders> GetBlockHeadersAsync(this RPCClient rpc, IList<int> blockHeights, CancellationToken cancellationToken)
		{
			var batch = rpc.PrepareBatch();
			var hashes = blockHeights.Select(h => batch.GetBlockHashAsync(h, cancellationToken)).ToArray();
			await batch.SendBatchAsync(cancellationToken);

			batch = rpc.PrepareBatch();
			var headers = hashes.Select(async h => await batch.GetBlockHeaderAsyncEx(await h, cancellationToken)).ToArray();
			await batch.SendBatchAsync(cancellationToken);

			return new BlockHeaders(headers.Select(h => h.GetAwaiter().GetResult()).Where(h => h is not null).ToList());
		}
		public static async Task<BlockHeaders> GetBlockHeadersAsync(this RPCClient rpc, IList<uint256> hashes, CancellationToken cancellationToken)
		{
			var batch = rpc.PrepareBatch();
			await batch.SendBatchAsync(cancellationToken);

			batch = rpc.PrepareBatch();
			var headers = hashes.Select(async h => await batch.GetBlockHeaderAsyncEx(h, cancellationToken)).ToArray();
			await batch.SendBatchAsync(cancellationToken);

			return new BlockHeaders(headers.Select(h => h.GetAwaiter().GetResult()).Where(h => h is not null).ToList());
		}

		public static async Task<RPCBlockHeader> GetBlockHeaderAsyncEx(this RPCClient rpc, uint256 blk, CancellationToken cancellationToken)
		{
			var header = await rpc.SendCommandAsync(new NBitcoin.RPC.RPCRequest("getblockheader", new[] { blk.ToString() })
			{
				ThrowIfRPCError = false
			}, cancellationToken);
			if (header.Result is null || header.Error is not null)
				return null;
			var response = header.Result;
			var confs = response["confirmations"].Value<long>();
			if (confs == -1)
				return null;

			var prev = response["previousblockhash"]?.Value<string>();
			return new RPCBlockHeader(
				blk,
				prev is null ? null : new uint256(prev),
				response["height"].Value<int>(),
				NBitcoin.Utils.UnixTimeToDateTime(response["time"].Value<long>()),
				new uint256(response["merkleroot"]?.Value<string>()));
		}

		public static async Task<SavedTransaction> TryGetRawTransaction(this RPCClient client, uint256 txId, CancellationToken cancellationToken)
		{
			var request = new RPCRequest(RPCOperations.getrawtransaction, new object[] { txId, true }) { ThrowIfRPCError = false };
			var response = await client.SendCommandAsync(request);
			if (response.Error == null && response.Result is JToken rpcResult && rpcResult["hex"] != null)
			{
				uint256 blockHash = null;
				long? blockHeight = null;
				if (rpcResult["blockhash"] != null)
				{
					blockHash = uint256.Parse(rpcResult.Value<string>("blockhash"));
					var blockHeader = await client.GetBlockHeaderAsyncEx(blockHash, cancellationToken);
					if (blockHeader is not null)
						blockHeight = blockHeader.Height;
					else
						blockHash = null;
				}
				DateTimeOffset timestamp = DateTimeOffset.UtcNow;
				if (rpcResult["time"] != null)
				{
					timestamp = NBitcoin.Utils.UnixTimeToDateTime(rpcResult.Value<long>("time"));
				}

				var rawTx = client.Network.Consensus.ConsensusFactory.CreateTransaction();
				rawTx.ReadWrite(Encoders.Hex.DecodeData(rpcResult.Value<string>("hex")), client.Network);
				return new SavedTransaction()
				{
					BlockHash = blockHash,
					BlockHeight = blockHeight,
					Timestamp = timestamp,
					Transaction = rawTx
				};
			}
			return null;
		}

		public async static Task<Dictionary<uint256, Transaction>> GetTransactionFromBlocks(this RPCClient rpc, HashSet<(uint256 BlockId, uint256 TransactionId)> txBlockIds, CancellationToken cancellationToken = default)
		{
			async Task<Dictionary<uint256, Transaction>> GetTransactionFromStoredBlocks(HashSet<(uint256 BlockId, uint256 TransactionId)> txBlockIds)
			{
				var result = new Dictionary<uint256, Transaction>();
				if (txBlockIds.Count == 0)
					return result;
				var batch = rpc.PrepareBatch();
				var fetching = txBlockIds.Select(b => (b.TransactionId, Fetching: batch.GetRawTransactionAsync(b.TransactionId, b.BlockId, false, cancellationToken))).ToArray();
				await batch.SendBatchAsync(cancellationToken);
				foreach (var f in fetching)
				{
					Transaction tx = null;
					try
					{
						tx = await f.Fetching;
					}
					catch
					{
					}
					if (tx is not null)
						result.TryAdd(f.TransactionId, tx);
				}
				return result;
			}
			async Task<HashSet<uint256>> FetchFromPeers(HashSet<uint256> blocks)
			{
				var downloaded = new HashSet<uint256>();
				if (blocks.Count == 0)
					return downloaded;
				var peers = (await rpc.GetPeersInfoAsync(cancellationToken))
									.Where(p => p.ServicesNames?.Contains("NETWORK") is true)
									.ToArray();
				NBitcoin.Utils.Shuffle(peers);
				foreach (var block in blocks)
				{
					foreach (var peer in peers)
					{
						try
						{
							var result = await rpc.GetBlockFromPeer(block, peer.Id, cancellationToken);
							if (result is GetBlockFromPeerResult.BlockHeaderMissing or GetBlockFromPeerResult.NeverSynched)
								goto end;
							if (result is GetBlockFromPeerResult.Fetched or GetBlockFromPeerResult.AlreadyDownloaded)
							{
								downloaded.Add(block);
								goto nextBlock;
							}
						}
						catch (RPCException e) when (e.RPCCode == RPCErrorCode.RPC_METHOD_NOT_FOUND) { return downloaded; }
						catch { }
					}
					nextBlock:;
				}
				end:;
				return downloaded;
			}

			var result = await GetTransactionFromStoredBlocks(txBlockIds);
			if (rpc.Capabilities?.CanGetBlockFromPeer is not true || result.Count == txBlockIds.Count)
				return result;

			int retryCount = 10;
			retry:
			var blockNotFound = txBlockIds.Where(t => !result.ContainsKey(t.TransactionId)).Select(t => t.BlockId).ToHashSet();
			var fetchedBlocks = await FetchFromPeers(blockNotFound);
			var newlyAvailable = txBlockIds.Where(t => !result.ContainsKey(t.TransactionId) && fetchedBlocks.Contains(t.BlockId)).ToHashSet();
			foreach (var kv in await GetTransactionFromStoredBlocks(newlyAvailable))
				result.Add(kv.Key, kv.Value);
			if (result.Count != txBlockIds.Count && retryCount > 0)
			{
				// Somehow, sometimes, we need to fetch more than once, or the block is not available during a short delay
				await Task.Delay(1000, cancellationToken);
				retryCount--;
				goto retry;
			}
			return result;
		}
		static Regex RangeRegex = new Regex("\\[\\s*(\\d+)\\s*,\\s*(\\d+)\\s*\\]", RegexOptions.ECMAScript);
		public async static Task ImportDescriptors(this RPCClient rpc, string descriptor, long from, long to, CancellationToken cancellationToken)
		{
			retry:
			try
			{
				// This way of handling error is strange, but this is because
				// for odd reasons, importdescriptors does not always return a RPC error in case
				// of error.
				var result = await rpc.SendCommandAsync(new RPCRequest()
				{
					Method = "importdescriptors",
					ThrowIfRPCError = true,
					Params = new[]
				{
					new JArray(
					new JObject()
					{
						["desc"] = descriptor,
						["timestamp"] = "now",
						["range"] = new JArray(from, to)
					})
				}
				}, cancellationToken);
				if (result.Result[0]["success"]?.Value<bool>() is true)
					return;
				new RPCResponse((JObject)result.Result[0]).ThrowIfError();
			}
			// Somehow, we can't import a disjoint range in the descriptor wallet
			// new range must include current range = [0, 999]
			catch (RPCException rpcEx) when (RangeRegex.IsMatch(rpcEx.Message))
			{
				var m = RangeRegex.Match(rpcEx.Message);
				var currentFrom = int.Parse(m.Groups[1].Value);
				var currentTo = int.Parse(m.Groups[2].Value);
				from = Math.Min(from, currentFrom);
				to = Math.Max(to, currentTo);
				if (from == currentFrom && to == currentTo)
					return;
				goto retry;
			}
			catch (RPCException rpcEx) when (rpcEx.RPCCode == RPCErrorCode.RPC_WALLET_ERROR &&
											 rpcEx.Message.Contains("Wallet is currently rescanning", StringComparison.OrdinalIgnoreCase))
			{
				await Task.Delay(1000, cancellationToken);
				goto retry;
			}
			throw new NotSupportedException($"Bug of NBXplorer (ERR 3083), please notify the developers");
		}
		public static async Task<Dictionary<uint256, Transaction>> GetRawTransactions(this RPCClient rpc, HashSet<uint256> txIds)
		{
			if (txIds.Count == 0)
				return new();
			var batch = rpc.PrepareBatch();
			var txs = txIds.Select(t => (Id: t, Tx: batch.GetRawTransactionAsync(t, false))).ToArray();
			await batch.SendBatchAsync();
			var res = new Dictionary<uint256, Transaction>();
			foreach (var txAsync in txs)
			{
				var tx = await txAsync.Tx;
				if (tx is not null)
					res.TryAdd(txAsync.Id, tx);
			}
			return res;
		}
		public static async Task<Dictionary<OutPoint, GetTxOutResponse>> GetTxOuts(this RPCClient rpc, IList<OutPoint> outpoints)
		{
			var batch = rpc.PrepareBatch();
			var txOuts = outpoints.Select(o => batch.GetTxOutAsync(o.Hash, (int)o.N, true)).ToArray();
			await batch.SendBatchAsync();
			var result = new Dictionary<OutPoint, GetTxOutResponse>();
			int i = 0;
			foreach (var txOut in txOuts)
			{
				var outpoint = outpoints[i];
				var r = await txOut;
				if (r != null)
					result.TryAdd(outpoint, r);
				i++;
			}
			return result;
		}
	}
}
