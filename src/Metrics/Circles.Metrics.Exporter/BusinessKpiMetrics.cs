using Prometheus;

namespace Circles.Metrics.Exporter;

/// <summary>
/// Prometheus metrics for Circles business KPIs.
/// These metrics are derived from PostgreSQL queries run periodically.
/// </summary>
public static class BusinessKpiMetrics
{
    // ===========================================
    // User Metrics
    // ===========================================

    public static readonly Gauge TotalHumans = Prometheus.Metrics
        .CreateGauge("circles_total_humans",
            "Total number of registered humans",
            new GaugeConfiguration { LabelNames = new[] { "version" } });

    public static readonly Gauge TotalOrganizations = Prometheus.Metrics
        .CreateGauge("circles_total_organizations",
            "Total number of registered organizations");

    public static readonly Gauge TotalGroups = Prometheus.Metrics
        .CreateGauge("circles_total_groups",
            "Total number of registered groups");

    public static readonly Gauge NewUsers = Prometheus.Metrics
        .CreateGauge("circles_new_users",
            "Number of new users in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Trust Network Metrics
    // ===========================================

    public static readonly Gauge ActiveTrusts = Prometheus.Metrics
        .CreateGauge("circles_active_trusts",
            "Total number of active trust relationships");

    public static readonly Gauge NewTrusts = Prometheus.Metrics
        .CreateGauge("circles_new_trusts",
            "Number of new trusts in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TrustChanges = Prometheus.Metrics
        .CreateGauge("circles_trust_changes",
            "Trust relationship changes in time window",
            new GaugeConfiguration { LabelNames = new[] { "window", "type" } });

    // ===========================================
    // Economic Metrics
    // ===========================================

    public static readonly Gauge TotalBackers = Prometheus.Metrics
        .CreateGauge("circles_total_backers",
            "Total number of LBP backers");

    public static readonly Gauge ActiveMinters = Prometheus.Metrics
        .CreateGauge("circles_active_minters",
            "Number of active minters in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge DailyMintVolume = Prometheus.Metrics
        .CreateGauge("circles_daily_mint_volume_crc",
            "CRC minted in the last 24 hours");

    public static readonly Gauge DailyTransferVolume = Prometheus.Metrics
        .CreateGauge("circles_daily_transfer_volume_crc",
            "CRC transferred in the last 24 hours");

    public static readonly Gauge DailyTransferCount = Prometheus.Metrics
        .CreateGauge("circles_daily_transfer_count",
            "Number of transfers in the last 24 hours");

    public static readonly Gauge UniqueTransactingAddresses = Prometheus.Metrics
        .CreateGauge("circles_unique_transacting_addresses",
            "Unique addresses transacting in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Group Metrics
    // ===========================================

    public static readonly Gauge GroupMembersTotal = Prometheus.Metrics
        .CreateGauge("circles_group_members_total",
            "Total number of group memberships");

    public static readonly Gauge GroupMintVolume = Prometheus.Metrics
        .CreateGauge("circles_group_mint_volume",
            "Group mint volume in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Profiles Metrics
    // ===========================================

    public static readonly Gauge ProfilesCreated = Prometheus.Metrics
        .CreateGauge("circles_profiles_created",
            "Number of profiles created",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // NEW: Dune Parity KPIs
    // ===========================================

    public static readonly Gauge DailyMintCount = Prometheus.Metrics
        .CreateGauge("circles_daily_mint_count",
            "Number of mint events in the last 24 hours");

    public static readonly Gauge NewBackers = Prometheus.Metrics
        .CreateGauge("circles_new_backers",
            "Number of new backers in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintingFraction14d = Prometheus.Metrics
        .CreateGauge("circles_minting_fraction_14d",
            "Fraction of registered humans who minted in the last 14 days (0-1)");

    public static readonly Gauge NewOrganizations = Prometheus.Metrics
        .CreateGauge("circles_new_organizations",
            "Number of new organizations in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge NewGroups = Prometheus.Metrics
        .CreateGauge("circles_new_groups",
            "Number of new groups in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Activity Rates (Minters/Spenders by window)
    // ===========================================

    public static readonly Gauge MintingRate = Prometheus.Metrics
        .CreateGauge("circles_minting_rate",
            "Fraction of registered humans who minted in time window (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge SpendingRate = Prometheus.Metrics
        .CreateGauge("circles_spending_rate",
            "Fraction of registered humans who sent transfers in time window (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TransferVolume = Prometheus.Metrics
        .CreateGauge("circles_transfer_volume_crc",
            "CRC transferred in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintVolume = Prometheus.Metrics
        .CreateGauge("circles_mint_volume_crc",
            "CRC minted in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TransferCount = Prometheus.Metrics
        .CreateGauge("circles_transfer_count",
            "Number of transfers in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge AverageTransferAmount = Prometheus.Metrics
        .CreateGauge("circles_average_transfer_amount_crc",
            "Average CRC amount per transfer in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MedianTransferAmount = Prometheus.Metrics
        .CreateGauge("circles_median_transfer_amount_crc",
            "Median CRC amount per transfer in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Sybil Detection Metrics
    // ===========================================

    public static readonly Gauge AccountsWithoutProfile = Prometheus.Metrics
        .CreateGauge("circles_accounts_no_profile",
            "Number of registered humans without a profile");

    public static readonly Gauge AccountsWithoutIncomingTrust = Prometheus.Metrics
        .CreateGauge("circles_accounts_no_trust_received",
            "Number of registered humans not trusted by anyone else");

    public static readonly Gauge BatchRegistrations = Prometheus.Metrics
        .CreateGauge("circles_batch_registrations",
            "Accounts registered in batches (same block) in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MintAndDrainAccounts = Prometheus.Metrics
        .CreateGauge("circles_mint_and_drain_accounts",
            "Accounts that minted but have zero balance",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge HighVolumeInviters = Prometheus.Metrics
        .CreateGauge("circles_high_volume_inviters",
            "Number of inviters with unusually high invitee counts",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge SuspiciousAccounts = Prometheus.Metrics
        .CreateGauge("circles_suspicious_accounts",
            "Accounts matching multiple sybil indicators (no profile, no trust, but minting)");

    public static readonly Gauge OrganicAccounts = Prometheus.Metrics
        .CreateGauge("circles_organic_accounts",
            "Accounts with profile and incoming trust (healthy accounts)");

    // ===========================================
    // Network Health Metrics
    // ===========================================

    public static readonly Gauge AverageTrustConnections = Prometheus.Metrics
        .CreateGauge("circles_average_trust_connections",
            "Average number of trust connections per registered human");

    public static readonly Gauge IsolatedAccounts = Prometheus.Metrics
        .CreateGauge("circles_isolated_accounts",
            "Accounts with zero trust connections in either direction");

    // ===========================================
    // Advanced Monetary/Economic Metrics
    // ===========================================

    public static readonly Gauge TotalCrcSupply = Prometheus.Metrics
        .CreateGauge("circles_total_crc_supply",
            "Total CRC in circulation (sum of all balances after demurrage)");

    public static readonly Gauge TotalMintedAllTime = Prometheus.Metrics
        .CreateGauge("circles_total_minted_all_time_crc",
            "Total CRC ever minted since genesis (before demurrage)");

    public static readonly Gauge DemurragePaid = Prometheus.Metrics
        .CreateGauge("circles_demurrage_paid_crc",
            "CRC lost to demurrage in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge MoneyVelocity = Prometheus.Metrics
        .CreateGauge("circles_money_velocity",
            "Money velocity (transfer volume / supply) in time window - how often CRC changes hands",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge ActiveBalanceHolders = Prometheus.Metrics
        .CreateGauge("circles_active_balance_holders",
            "Number of accounts with non-zero CRC balance");

    public static readonly Gauge AverageBalance = Prometheus.Metrics
        .CreateGauge("circles_average_balance_crc",
            "Average CRC balance per holder");

    public static readonly Gauge MedianBalance = Prometheus.Metrics
        .CreateGauge("circles_median_balance_crc",
            "Median CRC balance per holder");

    public static readonly Gauge GiniCoefficient = Prometheus.Metrics
        .CreateGauge("circles_gini_coefficient",
            "Gini coefficient of CRC distribution (0=perfect equality, 1=perfect inequality)");

    public static readonly Gauge TopHolderConcentration = Prometheus.Metrics
        .CreateGauge("circles_top_holder_concentration",
            "Percentage of total supply held by top N holders (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "top_n" } });

    public static readonly Gauge DailyActiveWallets = Prometheus.Metrics
        .CreateGauge("circles_daily_active_wallets",
            "Unique wallets that transacted in the last 24 hours (DAW)");

    public static readonly Gauge WeeklyActiveWallets = Prometheus.Metrics
        .CreateGauge("circles_weekly_active_wallets",
            "Unique wallets that transacted in the last 7 days (WAW)");

    public static readonly Gauge MonthlyActiveWallets = Prometheus.Metrics
        .CreateGauge("circles_monthly_active_wallets",
            "Unique wallets that transacted in the last 30 days (MAW)");

    public static readonly Gauge UserRetentionRate = Prometheus.Metrics
        .CreateGauge("circles_user_retention_rate",
            "Percentage of users who transacted in both current and previous period (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge FirstTimeTransactors = Prometheus.Metrics
        .CreateGauge("circles_first_time_transactors",
            "Users who made their first transfer in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TransferSizePercentile = Prometheus.Metrics
        .CreateGauge("circles_transfer_size_percentile_crc",
            "Transfer size at various percentiles (P10, P25, P75, P90)",
            new GaugeConfiguration { LabelNames = new[] { "percentile", "window" } });

    public static readonly Gauge MicroTransactionCount = Prometheus.Metrics
        .CreateGauge("circles_micro_transaction_count",
            "Number of transfers below 1 CRC in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge LargeTransactionCount = Prometheus.Metrics
        .CreateGauge("circles_large_transaction_count",
            "Number of transfers above 100 CRC in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge NetInflow = Prometheus.Metrics
        .CreateGauge("circles_net_inflow_crc",
            "Net new CRC minted in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    // ===========================================
    // Account Type Breakdown Metrics
    // ===========================================

    public static readonly Gauge GiniCoefficientByType = Prometheus.Metrics
        .CreateGauge("circles_gini_coefficient_by_type",
            "Gini coefficient of CRC distribution by account type (0=equality, 1=inequality)",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    public static readonly Gauge GiniCoefficientNonCustodial = Prometheus.Metrics
        .CreateGauge("circles_gini_coefficient_non_custodial",
            "Gini coefficient for humans excluding custodians/aggregators (accounts holding >50 unique tokens)");

    public static readonly Gauge TotalBalanceByType = Prometheus.Metrics
        .CreateGauge("circles_total_balance_by_type_crc",
            "Total CRC balance held by account type",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    public static readonly Gauge BalanceHolderCountByType = Prometheus.Metrics
        .CreateGauge("circles_balance_holders_by_type",
            "Number of accounts with non-zero balance by type",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    public static readonly Gauge AverageBalanceByType = Prometheus.Metrics
        .CreateGauge("circles_average_balance_by_type_crc",
            "Average CRC balance per holder by account type",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    public static readonly Gauge MedianBalanceByType = Prometheus.Metrics
        .CreateGauge("circles_median_balance_by_type_crc",
            "Median CRC balance per holder by account type",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    public static readonly Gauge TopHolderConcentrationByType = Prometheus.Metrics
        .CreateGauge("circles_top_holder_concentration_by_type",
            "Percentage of type's total supply held by top N holders (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "top_n", "account_type" } });

    public static readonly Gauge TopHolderConcentrationNonCustodial = Prometheus.Metrics
        .CreateGauge("circles_top_holder_concentration_non_custodial",
            "Percentage of non-custodial humans' supply held by top N holders (excludes accounts with >50 unique tokens)",
            new GaugeConfiguration { LabelNames = new[] { "top_n" } });

    public static readonly Gauge SupplyShareByType = Prometheus.Metrics
        .CreateGauge("circles_supply_share_by_type",
            "Percentage of total CRC supply held by account type (0-1)",
            new GaugeConfiguration { LabelNames = new[] { "account_type" } });

    // ===========================================
    // Infrastructure vs Economic Actors Holdings
    // ===========================================

    public static readonly Gauge InfrastructureHoldingsBalance = Prometheus.Metrics
        .CreateGauge("circles_infrastructure_holdings_crc",
            "Total CRC held by infrastructure addresses (Balancer vaults, group treasuries)");

    public static readonly Gauge EconomicActorsHoldingsBalance = Prometheus.Metrics
        .CreateGauge("circles_economic_actors_holdings_crc",
            "Total CRC held by economic actors (humans, groups, orgs - excluding infrastructure)");

    public static readonly Gauge InfrastructureHoldingsPercentage = Prometheus.Metrics
        .CreateGauge("circles_infrastructure_holdings_percentage",
            "Percentage of total CRC held by infrastructure addresses (0-100)");

    public static readonly Gauge EconomicActorsHoldingsPercentage = Prometheus.Metrics
        .CreateGauge("circles_economic_actors_holdings_percentage",
            "Percentage of total CRC held by economic actors (0-100)");

    public static readonly Gauge InfrastructureAddressCount = Prometheus.Metrics
        .CreateGauge("circles_infrastructure_address_count",
            "Number of infrastructure addresses with non-zero balance");

    public static readonly Gauge EconomicActorsCount = Prometheus.Metrics
        .CreateGauge("circles_economic_actors_count",
            "Number of economic actor addresses with non-zero balance");

    // ===========================================
    // Token Offers Metrics (GNO Bonus, Marketplace)
    // ===========================================

    public static readonly Gauge TokenOfferCyclesTotal = Prometheus.Metrics
        .CreateGauge("circles_token_offer_cycles_total",
            "Total number of token offer cycles created");

    public static readonly Gauge TokenOfferClaimsTotal = Prometheus.Metrics
        .CreateGauge("circles_token_offer_claims_total",
            "Total number of offer claims");

    public static readonly Gauge TokenOfferClaims = Prometheus.Metrics
        .CreateGauge("circles_token_offer_claims",
            "Number of offer claims in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TokenOfferUniqueClaimers = Prometheus.Metrics
        .CreateGauge("circles_token_offer_unique_claimers",
            "Unique accounts claiming offers in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TokenOfferCrcSpent = Prometheus.Metrics
        .CreateGauge("circles_token_offer_crc_spent",
            "Total CRC spent on offers in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TokenOfferCrcSpentTotal = Prometheus.Metrics
        .CreateGauge("circles_token_offer_crc_spent_total",
            "Total CRC spent on offers all time");

    public static readonly Gauge TokenOfferTokensReceived = Prometheus.Metrics
        .CreateGauge("circles_token_offer_tokens_received",
            "Total tokens received from offers in time window (in token units)",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge TokenOfferTokensReceivedTotal = Prometheus.Metrics
        .CreateGauge("circles_token_offer_tokens_received_total",
            "Total tokens received from offers all time (in token units)");

    public static readonly Gauge TokenOfferCurrentPriceInCrc = Prometheus.Metrics
        .CreateGauge("circles_token_offer_current_price_crc",
            "Current offer price in CRC (from latest NextOfferCreated)");

    public static readonly Gauge TokenOfferCurrentLimitInCrc = Prometheus.Metrics
        .CreateGauge("circles_token_offer_current_limit_crc",
            "Current offer limit in CRC per user (from latest NextOfferCreated)");

    public static readonly Gauge TokenOfferAcceptedCrcCount = Prometheus.Metrics
        .CreateGauge("circles_token_offer_accepted_crc_count",
            "Number of CRC tokens accepted by current offer");

    public static readonly Gauge TokenOfferAvgCrcPerClaim = Prometheus.Metrics
        .CreateGauge("circles_token_offer_avg_crc_per_claim",
            "Average CRC spent per offer claim (total_spent / claim_count)");

    // ===========================================
    // Payment Gateway Metrics
    // ===========================================

    public static readonly Gauge PaymentGatewaysTotal = Prometheus.Metrics
        .CreateGauge("circles_payment_gateways_total",
            "Total number of payment gateways created");

    public static readonly Gauge PaymentGatewaysCreated = Prometheus.Metrics
        .CreateGauge("circles_payment_gateways_created",
            "Payment gateways created in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PaymentGatewayPaymentsTotal = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_payments_total",
            "Total number of payments through gateways");

    public static readonly Gauge PaymentGatewayPayments = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_payments",
            "Payments through gateways in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PaymentGatewayVolume = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_volume_crc",
            "CRC volume through payment gateways in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PaymentGatewayVolumeTotal = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_volume_total_crc",
            "Total CRC volume through payment gateways all time");

    public static readonly Gauge PaymentGatewayUniquePayers = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_unique_payers",
            "Unique payers through gateways in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PaymentGatewayUniquePayees = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_unique_payees",
            "Unique payees through gateways in time window",
            new GaugeConfiguration { LabelNames = new[] { "window" } });

    public static readonly Gauge PaymentGatewayAvgPaymentSize = Prometheus.Metrics
        .CreateGauge("circles_payment_gateway_avg_payment_size",
            "Average payment size in CRC (total_volume / payment_count)");

    // ===========================================
    // Ecosystem Value & Price Metrics
    // ===========================================

    public static readonly Gauge GnoPriceUsd = Prometheus.Metrics
        .CreateGauge("circles_gno_price_usd",
            "Current GNO price in USD (from CoinGecko)");

    public static readonly Gauge CrcPriceUsd = Prometheus.Metrics
        .CreateGauge("circles_crc_price_usd",
            "Derived CRC price in USD (from GNO offer price)");

    public static readonly Gauge CrcPriceGno = Prometheus.Metrics
        .CreateGauge("circles_crc_price_gno",
            "CRC price in GNO (derived from token offers)");

    public static readonly Gauge TotalCrcSupplyUsd = Prometheus.Metrics
        .CreateGauge("circles_total_crc_supply_usd",
            "Total CRC supply valued in USD");

    public static readonly Gauge DailyMintVolumeUsd = Prometheus.Metrics
        .CreateGauge("circles_daily_mint_volume_usd",
            "CRC minted in last 24h valued in USD");

    public static readonly Gauge DailyTransferVolumeUsd = Prometheus.Metrics
        .CreateGauge("circles_daily_transfer_volume_usd",
            "CRC transferred in last 24h valued in USD");

    public static readonly Gauge PriceLastUpdated = Prometheus.Metrics
        .CreateGauge("circles_price_last_updated_timestamp",
            "Unix timestamp of last successful price update");

    public static readonly Gauge CrcPriceBalancerXdai = Prometheus.Metrics
        .CreateGauge("circles_crc_price_balancer_xdai",
            "Market-based dCRC price in xDAI from Balancer V3 (sCRC/xDAI ÷ convFactor)");

    public static readonly Gauge CrcPriceBalancerConvFactor = Prometheus.Metrics
        .CreateGauge("circles_crc_price_balancer_conv_factor",
            "Current sCRC→dCRC conversion factor (demurrage)");

    public static readonly Gauge PriceSource = Prometheus.Metrics
        .CreateGauge("circles_price_source",
            "Per-source boolean: label=coingecko|cached|fallback|balancer, value=1 when active",
            new GaugeConfiguration { LabelNames = new[] { "source" } });

    // ===========================================
    // Collection Metrics
    // ===========================================

    public static readonly Counter CollectionDuration = Prometheus.Metrics
        .CreateCounter("circles_kpi_collection_duration_seconds_total",
            "Total time spent collecting KPIs");

    public static readonly Counter CollectionErrors = Prometheus.Metrics
        .CreateCounter("circles_kpi_collection_errors_total",
            "Total number of KPI collection errors",
            new CounterConfiguration { LabelNames = new[] { "metric" } });

    public static readonly Gauge LastCollectionTimestamp = Prometheus.Metrics
        .CreateGauge("circles_kpi_last_collection_timestamp",
            "Unix timestamp of last successful KPI collection");
}
