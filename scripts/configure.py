"""Interactive local configuration. Never accepts secrets on command line."""
import getpass
import keyring
from web3 import Web3

if __name__=='__main__':
    print('Stores credentials in Windows Credential Manager for the current user.')
    address = input('Public BNB Chain wallet address (blank to retain): ').strip()
    if address:
        keyring.set_password('bosinai','wallet_address',Web3.to_checksum_address(address))
    api = getpass.getpass('1inch API key (blank to retain): ').strip()
    if api:
        keyring.set_password('bosinai','oneinch_api_key',api)
    search = getpass.getpass('Optional Brave Search API key for public stock posts (blank to retain): ').strip()
    if search:
        keyring.set_password('bosinai','brave_api_key',search)
    print('Saved. Paper/research mode does not need or store a private key.')
