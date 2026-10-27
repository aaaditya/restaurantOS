using RestaurantOS.Domain.Menu;
using RestaurantOS.Infrastructure.Persistence;

namespace RestaurantOS.Api.Modules.Menu;

/// <summary>Defaults for a new tenant: standard GST tax groups and, optionally, a small starter menu template.</summary>
public static class MenuSeeder
{
    public static void AddDefaults(AppDbContext db, Guid tenantId, bool sampleMenu)
    {
        var gst5 = new TaxGroup { TenantId = tenantId, Name = "GST 5%" };
        gst5.Components.Add(new TaxComponent { TaxGroupId = gst5.Id, Code = "CGST", RatePercent = 2.5m, SortOrder = 0 });
        gst5.Components.Add(new TaxComponent { TaxGroupId = gst5.Id, Code = "SGST", RatePercent = 2.5m, SortOrder = 1 });
        var gst18 = new TaxGroup { TenantId = tenantId, Name = "GST 18%" };
        gst18.Components.Add(new TaxComponent { TaxGroupId = gst18.Id, Code = "CGST", RatePercent = 9m, SortOrder = 0 });
        gst18.Components.Add(new TaxComponent { TaxGroupId = gst18.Id, Code = "SGST", RatePercent = 9m, SortOrder = 1 });
        db.TaxGroups.AddRange(gst5, gst18);

        if (!sampleMenu) return;

        var starters = new MenuCategory { TenantId = tenantId, Name = "Starters", SortOrder = 0 };
        var mains = new MenuCategory { TenantId = tenantId, Name = "Main Course", SortOrder = 1 };
        var breads = new MenuCategory { TenantId = tenantId, Name = "Breads", SortOrder = 2 };
        var drinks = new MenuCategory { TenantId = tenantId, Name = "Beverages", SortOrder = 3 };
        db.MenuCategories.AddRange(starters, mains, breads, drinks);

        var spice = new ModifierGroup { TenantId = tenantId, Name = "Spice level", MinSelect = 0, MaxSelect = 1 };
        spice.Options.AddRange([
            new ModifierOption { ModifierGroupId = spice.Id, Name = "Mild", Price = 0, SortOrder = 0 },
            new ModifierOption { ModifierGroupId = spice.Id, Name = "Medium", Price = 0, SortOrder = 1 },
            new ModifierOption { ModifierGroupId = spice.Id, Name = "Hot", Price = 0, SortOrder = 2 },
        ]);
        var addOns = new ModifierGroup { TenantId = tenantId, Name = "Add-ons", MinSelect = 0, MaxSelect = 3 };
        addOns.Options.AddRange([
            new ModifierOption { ModifierGroupId = addOns.Id, Name = "Extra cheese", Price = 30, SortOrder = 0 },
            new ModifierOption { ModifierGroupId = addOns.Id, Name = "Extra butter", Price = 20, SortOrder = 1 },
        ]);
        db.ModifierGroups.AddRange(spice, addOns);

        MenuItem Item(MenuCategory c, string name, decimal price, FoodType type, string station = "Main", TaxGroup? tax = null)
        {
            var item = new MenuItem
            {
                TenantId = tenantId, CategoryId = c.Id, Name = name, Price = price, FoodType = type, Station = station,
                TaxGroupId = (tax ?? gst5).Id,
            };
            db.MenuItems.Add(item);
            return item;
        }

        var paneerTikka = Item(starters, "Paneer Tikka", 260, FoodType.Veg, "Tandoor");
        paneerTikka.ModifierGroups.Add(new MenuItemModifierGroup { MenuItemId = paneerTikka.Id, ModifierGroupId = spice.Id });
        var chickenTikka = Item(starters, "Chicken Tikka", 320, FoodType.NonVeg, "Tandoor");
        chickenTikka.ModifierGroups.Add(new MenuItemModifierGroup { MenuItemId = chickenTikka.Id, ModifierGroupId = spice.Id });
        Item(starters, "Veg Spring Roll", 180, FoodType.Veg);

        var dal = Item(mains, "Dal Makhani", 240, FoodType.Veg);
        dal.Variants.AddRange([
            new ItemVariant { MenuItemId = dal.Id, Name = "Half", Price = 160, SortOrder = 0 },
            new ItemVariant { MenuItemId = dal.Id, Name = "Full", Price = 240, SortOrder = 1 },
        ]);
        var butterChicken = Item(mains, "Butter Chicken", 360, FoodType.NonVeg);
        butterChicken.Variants.AddRange([
            new ItemVariant { MenuItemId = butterChicken.Id, Name = "Half", Price = 220, SortOrder = 0 },
            new ItemVariant { MenuItemId = butterChicken.Id, Name = "Full", Price = 360, SortOrder = 1 },
        ]);
        butterChicken.ModifierGroups.Add(new MenuItemModifierGroup { MenuItemId = butterChicken.Id, ModifierGroupId = spice.Id });
        var pizza = Item(mains, "Margherita Pizza", 299, FoodType.Veg);
        pizza.ModifierGroups.Add(new MenuItemModifierGroup { MenuItemId = pizza.Id, ModifierGroupId = addOns.Id });

        Item(breads, "Butter Naan", 60, FoodType.Veg, "Tandoor");
        Item(breads, "Tandoori Roti", 30, FoodType.Veg, "Tandoor");

        Item(drinks, "Masala Chaas", 70, FoodType.Veg, "Bar");
        Item(drinks, "Fresh Lime Soda", 90, FoodType.Veg, "Bar");
        Item(drinks, "Cold Coffee", 140, FoodType.Veg, "Bar", gst18);
    }
}
