using System.Text.RegularExpressions;
using FluentAssertions;
using Rdm.Application.MasterData;
using Rdm.Infrastructure.Persistence;
using Rdm.Sap;

namespace Rdm.Tests;

public class MasterDataTests
{
    private static Dictionary<string, string> Kna1(string kunnr, string name1, string name2 = "", string name3 = "",
        string ktokd = "Z101", string loevm = "", string stceg = "", string stcd1 = "") => new()
    {
        ["KUNNR"] = kunnr, ["NAME1"] = name1, ["NAME2"] = name2, ["NAME3"] = name3, ["NAME4"] = "", ["SORTL"] = "S",
        ["STCD1"] = stcd1, ["STCEG"] = stceg, ["KTOKD"] = ktokd, ["LOEVM"] = loevm, ["ERDAT"] = "20220826",
        ["STRAS"] = "", ["ORT01"] = "", ["PSTLZ"] = "", ["REGIO"] = "", ["LAND1"] = "VN", ["TELF1"] = ""
    };

    private static Dictionary<string, string> Knvv(string kunnr, string spart = "11", string loevm = "") => new()
    {
        ["KUNNR"] = kunnr, ["VKORG"] = "G210", ["VTWEG"] = "10", ["SPART"] = spart, ["LOEVM"] = loevm
    };

    private static Dictionary<string, string> Knvp(string kunnr, string parvw, string kunn2, string parza = "000", string spart = "11") => new()
    {
        ["KUNNR"] = kunnr, ["VKORG"] = "G210", ["VTWEG"] = "10", ["SPART"] = spart, ["PARVW"] = parvw,
        ["PARZA"] = parza, ["KUNN2"] = kunn2, ["DEFPA"] = ""
    };

    [Fact]
    public void Sold_to_and_ship_to_are_built_from_partner_functions_excluding_deleted()
    {
        var kna1 = new[]
        {
            // Dữ liệu thật trên DRF-930
            Kna1("0000100166", "FUJI GREENTECH", "CÔNG TY TNHH VẬT LIỆU XÂY DỰNG", "FUJI GREENTECH", stceg: "6300336019"),
            Kna1("0000200379", "Hiệp Hòa Phát", "Công ty TNHH TMXD Hiệp Hòa Phát", ktokd: "Z104"),
            Kna1("0000200394", "Deleted ship-to", loevm: "X", ktokd: "Z104"),
            Kna1("0000300000", "Deleted sold-to", loevm: "X")
        };
        var knvv = new[] { Knvv("0000100166"), Knvv("0000300000"), Knvv("0000100166", spart: "10", loevm: "X") };
        var knvp = new[]
        {
            Knvp("0000100166", "AG", "0000100166"),
            Knvp("0000100166", "WE", "0000100166"),
            Knvp("0000100166", "WE", "0000200379", "001"),
            Knvp("0000100166", "WE", "0000200394", "002"),       // ship-to bị xóa
            Knvp("0000100166", "WE", "0000200379", spart: "10"),  // sales area bị xóa
            Knvp("0000300000", "AG", "0000300000")                // sold-to bị xóa
        };

        var result = SapMasterDataClient.BuildCustomerPartners(kna1, knvv, knvp,
            new Dictionary<string, string?> { ["Z101"] = "Sold-to Party" }, SoldToGroups);

        var soldTo = result.SoldTo.Should().ContainSingle().Subject;
        soldTo.CustomerNumber.Should().Be("0000100166");
        soldTo.CustomerName.Should().Be("CÔNG TY TNHH VẬT LIỆU XÂY DỰNG FUJI GREENTECH");
        soldTo.Name1.Should().Be("FUJI GREENTECH");
        soldTo.TaxNumber.Should().Be("6300336019");
        soldTo.CustomerType.Should().Be("Z101");
        soldTo.CustomerTypeName.Should().Be("Sold-to Party");
        soldTo.CreatedOn.Should().Be(new DateOnly(2022, 8, 26));

        // Quan hệ: bỏ ship-to bị xóa và sales area bị xóa.
        result.Relations.Select(r => (r.ShipTo, r.PartnerCounter)).Should().BeEquivalentTo(new[]
        {
            ("0000100166", 0), ("0000200379", 1)
        });
        result.Relations.Should().OnlyContain(r => r.SoldTo == "0000100166" && r.Division == "11");

        // Danh mục ship-to: mỗi ship-to 1 dòng, có loại khách hàng.
        result.ShipTo.Select(s => s.CustomerNumber).Should().BeEquivalentTo(new[] { "0000100166", "0000200379" });
        var hhp = result.ShipTo.Single(s => s.CustomerNumber == "0000200379");
        hhp.CustomerName.Should().Be("Công ty TNHH TMXD Hiệp Hòa Phát");
        hhp.CustomerType.Should().Be("Z104");
    }

    [Fact]
    public void Ship_to_used_by_several_sold_to_appears_once_in_catalog()
    {
        var kna1 = new[] { Kna1("A", "Sold A"), Kna1("B", "Sold B"), Kna1("S", "Ship", ktokd: "Z104") };
        var knvv = new[] { Knvv("A"), Knvv("B") };
        var knvp = new[]
        {
            Knvp("A", "AG", "A"), Knvp("B", "AG", "B"),
            Knvp("A", "WE", "S", "001"), Knvp("B", "WE", "S", "001")
        };

        var result = SapMasterDataClient.BuildCustomerPartners(kna1, knvv, knvp, new Dictionary<string, string?>(), SoldToGroups);

        result.ShipTo.Should().ContainSingle(s => s.CustomerNumber == "S");
        result.Relations.Where(r => r.ShipTo == "S").Select(r => r.SoldTo).Should().BeEquivalentTo(new[] { "A", "B" });
    }

    private static readonly string[] SoldToGroups = { "Z101", "Z001" };

    [Fact]
    public void Ship_to_account_group_is_not_a_sold_to_even_with_self_ag_partner()
    {
        var kna1 = new[] { Kna1("0000200379", "Hiệp Hòa Phát", ktokd: "Z104") };
        var result = SapMasterDataClient.BuildCustomerPartners(kna1, new[] { Knvv("0000200379") },
            new[] { Knvp("0000200379", "AG", "0000200379") }, new Dictionary<string, string?>(), SoldToGroups);

        result.SoldTo.Should().BeEmpty();
    }

    [Fact]
    public void Customer_name_falls_back_to_name1_and_tax_number_to_stcd1()
    {
        var c = Kna1("1", "MINH NHỰT", stcd1: "0301234567");
        SapMasterDataClient.FullName(c).Should().Be("MINH NHỰT");

        var result = SapMasterDataClient.BuildCustomerPartners(new[] { c }, new[] { Knvv("1") }, new[] { Knvp("1", "AG", "1") },
            new Dictionary<string, string?>(), SoldToGroups);
        result.SoldTo.Single().TaxNumber.Should().Be("0301234567");
    }

    [Fact]
    public void Sap_data_json_keeps_all_non_empty_fields_except_client_and_vietnamese_is_readable()
    {
        var json = SapMasterDataClient.ToSapData(new Dictionary<string, string>
        {
            ["MANDT"] = "930", ["WERKS"] = "G210", ["NAME1"] = "Tây Ninh", ["NAME2"] = ""
        });
        json.Should().Be("{\"WERKS\":\"G210\",\"NAME1\":\"Tây Ninh\"}");
    }

    /// <summary>TVP map theo thứ tự cột: registry trong repository phải khớp table type trong script SQL.</summary>
    [Fact]
    public void Repository_column_order_matches_table_types_in_sql_script()
    {
        // Theo thứ tự migration: định nghĩa ở script sau thay thế script trước.
        var batches = new[] { "MasterData_Procedures.sql", "MasterData_SoldToShipTo.sql", "MasterData_Material.sql", "MasterData_Finance.sql" }
            .SelectMany(SqlScripts.Batches).ToList();
        foreach (var (type, target) in MasterDataRepository.Targets)
        {
            var ddl = batches.Last(b => b.Contains($"CREATE TYPE {target.TableType} ", StringComparison.Ordinal));
            var body = ddl[(ddl.IndexOf($"CREATE TYPE {target.TableType} ", StringComparison.Ordinal))..]; body = body[(body.IndexOf('(') + 1)..];
            // Có dòng chứa nhiều cột → tách theo dấu phẩy; bỏ mệnh đề PRIMARY KEY (...).
            var columns = body.Split(',')
                .Select(s => s.Trim())
                .Where(s => !s.StartsWith("PRIMARY KEY", StringComparison.Ordinal) && Regex.IsMatch(s, @"^\w+\s+(nvarchar|varchar|int|bit|date|datetime2)"))
                .Select(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
                .ToList();

            columns.Should().Equal(target.Columns, $"{type.Name} ↔ {target.TableType}");
            batches.Should().Contain(b => b.Contains($"CREATE PROCEDURE {target.Procedure} ", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Data_table_converts_date_only_and_nulls()
    {
        var rows = new[] { new SoldToParty { CustomerNumber = "1", CustomerName = "A", CustomerType = "Z101", CreatedOn = new DateOnly(2022, 8, 26) } };
        using var table = MasterDataRepository.ToDataTable(rows, MasterDataRepository.Targets[typeof(SoldToParty)].Columns);

        table.Columns["CreatedOn"]!.DataType.Should().Be(typeof(DateTime));
        table.Rows[0]["CreatedOn"].Should().Be(new DateTime(2022, 8, 26));
        table.Rows[0]["Name1"].Should().Be(DBNull.Value);
    }
}


