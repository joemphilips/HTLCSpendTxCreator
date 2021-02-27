using System;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net.Security;
using NBitcoin;
using System.Threading.Tasks;
using NBitcoin.DataEncoders;

namespace HTLCSpendTxCreator
{
  enum InputType
  {
    Wsh,
    ShWsh
  }

  class Program
  {
    private static RootCommand BuildRootCommand()
    {
      var root = new RootCommand();
      root.Name = "htlcspender";
      root.Description = "Build TX to spend HTLC without change.";
      Func<Func<byte[], string>, ValidateSymbol<OptionResult>> hexValidator = (additionalValidator) => r =>
      {
        var msg = "Must be hex ";
        var v = r.GetValueOrDefault<string>();
        if (String.IsNullOrEmpty(v))
          return msg;
        var hexEncoder = new HexEncoder();
        byte[] b;
        try
        {
          b = hexEncoder.DecodeData(v);
        }
        catch (Exception e)
        {
          return msg + e.Message;
        }

        var res2 = additionalValidator(b);
        return String.IsNullOrEmpty(res2) ? null : res2;
      };

      var redeemOpt = new Option<string>(new[] {"--redeem", "--redeem-script", "-r"}, "redeem script hex")
      {
        IsRequired = true
      };
      redeemOpt.AddValidator(hexValidator(_ => null));
      root.AddOption(redeemOpt);

      var txidOpt = new Option<string>(new[] {"--txid", "-t"}, "Input HTLC tx id")
      {
        Argument = new Argument<string>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      };
      txidOpt.AddValidator(hexValidator(b => b.Length != 32 ? "txid must be 32 bytes" : null));
      root.AddOption(txidOpt);

      var feeRateOpt = new Option<int>(new[] {"--feerate", "--fee", "-f"}, "feerate for the tx (satoshi per vbytes)")
      {
        Argument = new Argument<int>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      };
      root.AddOption(feeRateOpt);

      root.AddOption(new Option<int>(new []{"--amount"}, "Input amount (in satoshi)")
      {
        Argument = new Argument<int>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      });
      root.AddOption(new Option<int>(new []{"--prev-outindex", "--outindex"}, "Previous UTXO's output index")
      {
        Argument = new Argument<int>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      });
      var privKeyOpt = new Option<string>(new[] {"--key", "--privkey", "-k"}, "Private key hex used for signing")
      {
        Argument = new Argument<string>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      };
      privKeyOpt.AddValidator(hexValidator(b => b.Length != 32 ? "private key must be 32 bytes in hex" : null));
      root.AddOption(privKeyOpt);
      var preimageOpt = new Option<string>(new[] {"--preimage", "-p"}, "Preimage hex to claim the HTLC")
      {
        Argument = new Argument<string>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      };
      preimageOpt.AddValidator(hexValidator(b => b.Length != 32 ? "preimage must be 32 bytes in hex" : null));
      root.AddOption(preimageOpt);
      root.AddOption(new Option<string>(new []{"--address"}, "Your address to send funds")
      {
        Argument = new Argument<string>
        {
          Arity = ArgumentArity.ExactlyOne
        },
        IsRequired = true
      });
      var o = new Option<InputType>(new[] {"--input-type", "--type"}, "type of the previous output. (default: wsh)")
      {
        Argument = new Argument<InputType>
        {
          Arity = ArgumentArity.ZeroOrOne
        }.FromAmong("wsh", "sh-wsh")
      };
      root.AddOption(o);
      var networkOpts =
        new Option<string>(new[] {"-n", "--network"}, "Set the network from (mainnet, testnet, regtest) (default:mainnet)")
        {
          Argument = new Argument<string>()
          {
            Arity = ArgumentArity.ZeroOrOne
          }.FromAmong("mainnet", "testnet", "regtest")
        };
      root.AddOption(networkOpts);
      return root;
    }

    static void Run(string redeem, string txidHex, int feeRateSatPerKBytes, int amountSatoshi, int prevOutIndex, string privKeyHex, string preimageHex, string addr, InputType? inputType = null, string networkName = null)
    {
      var n = networkName == null || networkName == "mainnet" ? Network.Main :
        networkName == "testnet" ? Network.TestNet :
        networkName == "regtest" ? Network.RegTest : null;
      if (n is null) throw new Exception($"Unsupported network {networkName}");
      var txb = n.CreateTransactionBuilder();
      var hex = new HexEncoder();
      Console.WriteLine($"Redeem Hex {redeem}");
      var redeemScript =
        new Script(hex.DecodeData(redeem));
      var swapTxId = uint256.Parse(txidHex);
      var swapVout = prevOutIndex;
      var spk = inputType == null || inputType == InputType.Wsh ? redeemScript.WitHash.ScriptPubKey :
        inputType == InputType.ShWsh ? redeemScript.WitHash.ScriptPubKey.Hash.ScriptPubKey : default;
      var txo = new TxOut(amountSatoshi, spk);
      var c = new Coin(new OutPoint(swapTxId, swapVout), txo);
      var sc = new ScriptCoin(c, redeemScript);
      txb.AddCoins(sc);
      var feeRate = new FeeRate(feeRateSatPerKBytes / 1000m);
      txb.SendEstimatedFees(feeRate);
      var claimPrivKey = new Key(hex.DecodeData(privKeyHex));
      txb.AddKeys(claimPrivKey);
      var alicePayoutAddress = BitcoinAddress.Create(addr, n);
      txb.SendAll(alicePayoutAddress);
      var tx = txb.BuildTransaction(false);
      var signature = tx.SignInput(claimPrivKey, sc);
      var preimage = hex.DecodeData(preimageHex);
      var witnessItems =
        new WitScript(Op.GetPushOp(preimage)) +
        new WitScript(Op.GetPushOp(signature.ToBytes())) +
        new WitScript(Op.GetPushOp(redeemScript.ToBytes()));
      tx.Inputs[0].WitScript = witnessItems;
      Console.WriteLine(tx.ToHex());
    }

    static void Main(string[] args)
    {
      var rootCommand = BuildRootCommand();
      // rootCommand.Handler = CommandHandler.Create<string, string, int, int, int, string, string, string>(Run);
      rootCommand.Handler = CommandHandler.Create((ParseResult pr) =>
      {
        Run(
          pr.RootCommandResult.OptionResult("--redeem")?.GetValueOrDefault<string>(),
          pr.RootCommandResult.OptionResult("--txid")?.GetValueOrDefault<string>(),
          pr.RootCommandResult.OptionResult("--fee")!.GetValueOrDefault<int>(),
          pr.RootCommandResult.OptionResult("--amount")!.GetValueOrDefault<int>(),
          pr.RootCommandResult.OptionResult("--outindex")!.GetValueOrDefault<int>(),
          pr.RootCommandResult.OptionResult("--privkey")?.GetValueOrDefault<string>(),
          pr.RootCommandResult.OptionResult("--preimage")?.GetValueOrDefault<string>(),
          pr.RootCommandResult.OptionResult("--address")?.GetValueOrDefault<string>(),
          pr.RootCommandResult.OptionResult("--input-type")?.GetValueOrDefault<InputType>(),
          pr.RootCommandResult.OptionResult("--network")?.GetValueOrDefault<string>()
        );
      });
      var cli = new CommandLineBuilder(rootCommand).UseDefaults().Build();
      cli.Invoke(args);
    }
  }
}
