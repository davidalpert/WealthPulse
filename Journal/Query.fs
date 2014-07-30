﻿namespace WealthPulse

open System
open Journal

module Query =

    type QueryFilters = {
        AccountsWith: string list option;
        ExcludeAccountsWith: string list option;
        PeriodStart: DateTime option;
        PeriodEnd: DateTime option;
    }

    type AccountBalance = {
        Account: string;
        Balance: Amount;
        RealBalance: Amount option;
        Commodity: Amount option;
        Price: Amount option;
        PriceDate: DateTime option;
    }

    type CommodityUsage = {
        Symbol: Symbol;
        FirstAppeared: DateTime;
        ZeroBalanceDate: DateTime option;
    } 

    module private Support =
        
        // account contains one of "terms" if "terms" provided, otherwise defaultValue
        let containsOneOf defaultValue termsOption (account :string) =
            match termsOption with
            | Some terms -> List.exists (fun (token :string) -> (account.ToLower().Contains(token.ToLower()))) terms
            | None       -> defaultValue

        
        let withinPeriod date periodStartOption periodEndOption =
            match periodStartOption, periodEndOption with
            | Some periodStart, Some periodEnd -> periodStart <= date && date <= periodEnd
            | Some periodStart, None -> periodStart <= date
            | None, Some periodEnd -> date <= periodEnd
            | _, _ -> true


        /// Apply filters to retrieve journal entries
        let filterEntries (filters : QueryFilters) (journal : Journal) =
            // apply account filters to construct a set of accounts
            // TODO: Not fond of the call to "containsOneOf" needing the default parameter, there must be a better way...
            let accounts = 
                journal.AllAccounts
                |> Set.filter (containsOneOf true filters.AccountsWith)
                |> Set.filter (containsOneOf false filters.ExcludeAccountsWith >> not)

            // filter entries based on selected accounts and period filters
            // TODO: Can I separate account filter and period filter and pipeline them within the List.filter?
            journal.Entries
            |> List.filter (fun entry -> (Set.contains entry.Account accounts) 
                                         && (withinPeriod entry.Header.Date filters.PeriodStart filters.PeriodEnd))

        
        /// Returns a list of AccountBalance records summed for all accounts in the account lineage for each entry in entries.
        /// For accounts with commodities, we also calculate the total number of units, which is not propagated to parent accounts.
        /// TODO: Real value, price, and price date for commodities will be calculated in a separate step.
        let calculateAccountBalances entries =
            let accountBalanceMap = new System.Collections.Generic.Dictionary<string, AccountBalance>()
            let ensureSymbolsMatch (amount1 : Amount) (amount2 : Amount) =
                match amount1.Symbol, amount2.Symbol with
                | Some s1, Some s2 when s1 <> s2 -> failwith (sprintf "Symbols do not match: %s and %s" s1 s2)
                | Some s1, None                  -> failwith (sprintf "Symbols do not match: %s and <no symbol>" s1)
                | None, Some s2                  -> failwith (sprintf "Symbols do not match: <no symbol> and %s" s2)
                | otherwise                      -> ()
            let createAccountBalanceForAccount (account : string) (entry : Entry) =
                match account = entry.Account with
                | true  ->  {
                                Account = account;
                                Balance = entry.Amount;
                                RealBalance = None;
                                Commodity = entry.Commodity;
                                Price = None;
                                PriceDate = None;
                            }
                | false ->  {
                                Account = account;
                                Balance = entry.Amount;
                                RealBalance = None;
                                Commodity = None;
                                Price = None;
                                PriceDate = None;
                            }
            let updateAccountBalanceForAccount (account : string) (balance : AccountBalance) (entry : Entry) =
                do ensureSymbolsMatch balance.Balance entry.Amount
                match account = entry.Account, entry.Commodity with
                | true, Some _  ->  do ensureSymbolsMatch balance.Commodity.Value entry.Commodity.Value
                                    { balance with Balance = { balance.Balance with Amount = balance.Balance.Amount + entry.Amount.Amount };
                                                   Commodity = Some { balance.Commodity.Value with Amount = balance.Commodity.Value.Amount + entry.Commodity.Value.Amount } }
                | otherwise     ->  { balance with Balance = { balance.Balance with Amount = balance.Balance.Amount + entry.Amount.Amount } }
            let addAmountForAccounts entry = 
                fun account -> 
                    match accountBalanceMap.ContainsKey(account) with
                    | true  -> accountBalanceMap.[account] <- updateAccountBalanceForAccount account accountBalanceMap.[account] entry
                    | false -> accountBalanceMap.[account] <- createAccountBalanceForAccount account entry
            let forEachAccountInLineageAddAmount entry =
                List.iter (addAmountForAccounts entry) entry.AccountLineage
            do List.iter forEachAccountInLineageAddAmount entries
            accountBalanceMap.Keys
            |> Seq.map (fun key -> accountBalanceMap.[key])
            |> Seq.toList


        /// Groups entries by header and returns (date, payee, entries) tuples
        let calculateRegisterLines entries =
            let runningTotal = ref 0M
            // return (account, amount, total) for an entry
            // using localized side-effects here to simplify computation of running total
            let calculateEntryLine (entry : Entry) =
                runningTotal := !runningTotal + entry.Amount.Amount
                (entry.Account, entry.Amount.Amount, !runningTotal)
            entries
            |> Seq.groupBy (fun entry -> entry.Header)
            |> Seq.map (fun (header, entries) -> header.Date, header.Description, Seq.map calculateEntryLine entries |> Seq.toList |> List.rev)

            

    open Support


    /// Returns a tuple of (accountBalances, totalBalance) that match the filters in parameters,
    /// where accountBalances has type AccountBalance
    let balance (filters : QueryFilters) (journal : Journal) =
        let filteredEntries = filterEntries filters journal

        // sum to get account balances, discard accounts with 0 balance
        let accountBalances =
            filteredEntries
            |> calculateAccountBalances
            |> List.filter (fun accountBalance -> accountBalance.Balance.Amount <> 0M)
        
        // filter parent accounts where amount is the same as the (assumed) single child
        let accountBalances =
            let existsChildAccountWithSameAmount allBalances accountBalance =
                allBalances
                |> List.exists (fun otherAccountBalance -> otherAccountBalance.Account.StartsWith(accountBalance.Account) 
                                                           && otherAccountBalance.Account.Length > accountBalance.Account.Length 
                                                           && otherAccountBalance.Balance.Amount = accountBalance.Balance.Amount)
                |> not
            accountBalances
            |> List.filter (existsChildAccountWithSameAmount accountBalances)

        // calculate total balance
        let totalBalance = 
            accountBalances
            |> List.filter (fun accountBalance -> Set.contains accountBalance.Account journal.MainAccounts)
            |> List.map (fun accountBalance -> accountBalance.Balance)
            |> List.reduce (fun balance1 balance2 -> {balance1 with Amount = balance1.Amount + balance2.Amount})

        (accountBalances, totalBalance)


    /// Returns a list of (date, payee, entries) tuples that match the filters,
    /// where lines is a list of (account, amount, total) tuples.
    let register (filters : QueryFilters) (journal : Journal) =
        let filteredEntries = filterEntries filters journal
        filteredEntries
        |> calculateRegisterLines
        |> Seq.toList
        |> List.rev


    /// Returns a sorted list of (payee, amount) tuples
    let outstandingPayees (journal : Journal) =
        let calculatePayeeAmounts (payees : Map<string,decimal>) (entry : Entry) =
            if entry.Account.StartsWith("Assets:Receivables:") || entry.Account.StartsWith("Liabilities:Payables:") then 
                let payee = entry.Account.Replace("Assets:Receivables:", "").Replace("Liabilities:Payables:", "")
                let currentAmount = if payees.ContainsKey(payee) then payees.[payee] else 0M
                Map.add payee (currentAmount + entry.Amount.Amount) payees
            else payees
        journal.Entries
        |> List.fold calculatePayeeAmounts Map.empty
        |> Map.filter (fun _ amount -> amount <> 0M)
        |> Map.toList


    /// Returns a list of commodities used in the journal
    let identifyCommodities (journal : Journal) =
        let buildCommodityMap map entry =
            match entry.Amount.Symbol with
            | Some symbol -> match Map.tryFind symbol map with
                             | Some cu -> match cu.FirstAppeared > entry.Header.Date with
                                          | true -> Map.add symbol {cu with FirstAppeared = entry.Header.Date} map
                                          | false -> map 
                             | otherwise -> Map.add symbol {Symbol = symbol; FirstAppeared = entry.Header.Date; ZeroBalanceDate = None;} map
            | otherwise -> map
        let determineZeroBalanceDate entries symbol (cu : CommodityUsage) =
            let entriesWithSymbol = entries
                                    |> List.filter (fun (e : Entry) -> match e.Amount.Symbol with
                                                                       | Some s when s = symbol -> true
                                                                       | otherwise -> false)
            let balance = entriesWithSymbol |> List.fold (fun balance entry -> balance + entry.Amount.Amount) 0M
            let lastDate = entriesWithSymbol 
                           |> List.fold (fun date entry -> if entry.Header.Date > date then entry.Header.Date else date) (List.head entriesWithSymbol).Header.Date
            match balance with
            | 0M -> {cu with ZeroBalanceDate = Some lastDate}
            | otherwise -> cu
        journal.Entries
        |> List.fold buildCommodityMap Map.empty
        |> Map.map (determineZeroBalanceDate journal.Entries)
