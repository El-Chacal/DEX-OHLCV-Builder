# DEX-OHLCV-Builder
A simple bot in C# that compiles data from the Ethereum or BSC blockchain into OHLCV data. Still in the early phase, so improvements are welcome.

This bot was tested exclusively on the BSC Network but should work just fine on Ethereum.

The bot requires Smart Contract Data to be already available under the format presented in the class ContractDetails to be able to perform. 

Not all the contract info are required to perform accurate calculations. You will only need the decimals of each contract. The rest is implemented as part of scam prevention, and you don't need it if your purpose is just to compile the OHLCV data.

Each document contains the OHLCV data of a specific Liquidity Pool, the LP is the used identifier of each document.

Token0 and Token1 are the tokens used to create the LP and should be saved in the DB exactly the same way they are stored on the blockchain, same should apply for the decimals to make sure the computation is correct and easy to read.

This bot uses RavenDB 5.4.5, Nethereum WebSocketClient 4.12.0, Nethereum Reactive 4.12.0, and Nethereum Web3 4.12.0.

Data is stored within the DB in the format of a dictionnary of strings (check class TickData to understand the model), as I found that it is the most efficient way so far to keep the size of the documents small and accessible quickly as more and more data comes.

It is based on the BlockProcessor and the LogProcessor. The first one is only used to get blocks timestamps, while the second is in charge of getting the swap events.

While some of you might say that the BlockProcessor alone should suffice in getting both information, it wasn't the case for me, and the BlockProcessor was extremely slow when I tried to get everything from it, hence the use of the LogProcessor.

The Data compiled is on the 1-minute timeframe.

Next step is to use get the Sync events also in order to populate reserve0 and reserve1 values to keep track of the supplies and liquidity within the LP.
